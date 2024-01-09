﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Extensions;
using WalletWasabi.Helpers;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.Wallets;

namespace BTCPayServer.Plugins.Wabisabi;

public class BTCPayCoinjoinCoinSelector : IRoundCoinSelector
{
    private readonly BTCPayWallet _wallet;

    public BTCPayCoinjoinCoinSelector(BTCPayWallet wallet)
    {
        _wallet = wallet;
    }

    public async Task<ImmutableList<SmartCoin>> SelectCoinsAsync(
        (IEnumerable<SmartCoin> Candidates, IEnumerable<SmartCoin> Ineligible) coinCandidates,
        UtxoSelectionParameters utxoSelectionParameters,
        Money liquidityClue, SecureRandom secureRandom)
    {
        SmartCoin[] FilterCoinsMore(IEnumerable<SmartCoin> coins)
        {
            return coins
                .Where(coin => utxoSelectionParameters.AllowedInputScriptTypes.Contains(coin.ScriptType))
                .Where(coin => utxoSelectionParameters.AllowedInputAmounts.Contains(coin.Amount))
                .Where(coin =>
                {
                    var effV = coin.EffectiveValue(utxoSelectionParameters.MiningFeeRate,
                        utxoSelectionParameters.CoordinationFeeRate);
                    var percentageLeft = (effV.ToDecimal(MoneyUnit.BTC) / coin.Amount.ToDecimal(MoneyUnit.BTC));
                    // filter out low value coins where 50% of the value would be eaten up by fees
                    return effV > Money.Zero && percentageLeft >= 0.5m;
                })
                .Where(coin =>
                {
                    if (!_wallet.WabisabiStoreSettings.PlebMode &&
                        _wallet.WabisabiStoreSettings.CrossMixBetweenCoordinatorsMode ==
                        WabisabiStoreSettings.CrossMixMode.Always)
                    {
                        return true;
                    }
                    if (!coin.HdPubKey.Labels.Contains("coinjoin") || coin.HdPubKey.Labels.Contains(utxoSelectionParameters.CoordinatorName))
                    {
                        return true;
                    }

                    if (_wallet.WabisabiStoreSettings.PlebMode ||
                        _wallet.WabisabiStoreSettings.CrossMixBetweenCoordinatorsMode ==
                        WabisabiStoreSettings.CrossMixMode.WhenFree)
                    {
                        return coin.Amount <= utxoSelectionParameters.CoordinationFeeRate.PlebsDontPayThreshold;
                    }

                    return false;
                    
                }).ToArray();
        }

        var candidates =
            FilterCoinsMore(coinCandidates.Candidates);
        var ineligibleCoins =
            FilterCoinsMore(coinCandidates.Ineligible);
        
        var payments =
            (_wallet.BatchPayments
                ? await _wallet.DestinationProvider.GetPendingPaymentsAsync(utxoSelectionParameters)
                : Array.Empty<PendingPayment>()).ToArray();
        
        var maxPerType = new Dictionary<AnonsetType, int>();

        var attemptingTobeParanoid = payments.Any() && _wallet.WabisabiStoreSettings.ParanoidPayments;
        var attemptingToMixToOtherWallet = string.IsNullOrEmpty(_wallet.WabisabiStoreSettings.MixToOtherWallet);
        selectCoins:
        maxPerType.Clear();
        if (attemptingTobeParanoid || attemptingToMixToOtherWallet)
        {
            maxPerType.Add(AnonsetType.Red,0);
            maxPerType.Add(AnonsetType.Orange,0);
        }
        
        if (_wallet.RedCoinIsolation)
        {
            maxPerType.TryAdd(AnonsetType.Red, 1);
        }

        var isLowFee = utxoSelectionParameters.MiningFeeRate.SatoshiPerByte <= _wallet.LowFeeTarget;
        var consolidationMode = _wallet.ConsolidationMode switch
        {
            ConsolidationModeType.Always => true,
            ConsolidationModeType.Never => false,
            ConsolidationModeType.WhenLowFee => isLowFee,
            ConsolidationModeType.WhenLowFeeAndManyUTXO => isLowFee && candidates.Count() > BTCPayWallet.HighAmountOfCoins,
            _ => throw new ArgumentOutOfRangeException()
        };
        Dictionary<AnonsetType, int> idealMinimumPerType = new Dictionary<AnonsetType, int>()
            {{AnonsetType.Red, 1}, {AnonsetType.Orange, 1}, {AnonsetType.Green, 1}};

        var solution = await SelectCoinsInternal(utxoSelectionParameters, candidates, ineligibleCoins,payments,
            Random.Shared.Next(10, 31),
            maxPerType,
            idealMinimumPerType,
            consolidationMode, liquidityClue, secureRandom);

        if (attemptingTobeParanoid && !solution.HandledPayments.Any())
        {
            attemptingTobeParanoid = false;
            payments = Array.Empty<PendingPayment>();
            goto selectCoins;
        }

        if (attemptingToMixToOtherWallet && !solution.Coins.Any())
        {
            // check that we have enough coins to mix to other wallet
            attemptingToMixToOtherWallet = false;
            goto selectCoins;
        }
        _wallet.LogTrace(solution.ToString());
        return solution.Coins.ToImmutableList();
    }

    private async Task<SubsetSolution> SelectCoinsInternal(UtxoSelectionParameters utxoSelectionParameters,
        IEnumerable<SmartCoin> coins,IEnumerable<SmartCoin> ineligibleCoins, IEnumerable<PendingPayment> pendingPayments,
        int maxCoins,
        Dictionary<AnonsetType, int> maxPerType, Dictionary<AnonsetType, int> idealMinimumPerType,
        bool consolidationMode, Money liquidityClue, SecureRandom random)
    {
        // Sort the coins by their anon score and then by descending order their value, and then slightly randomize in 2 ways:
        //attempt to shift coins that comes from the same tx AND also attempt to shift coins based on percentage probability
        var remainingCoins = SlightlyShiftOrder(RandomizeCoins(
            coins.OrderBy(coin => coin.CoinColor(_wallet.AnonScoreTarget)).ThenByDescending(x =>
                    x.EffectiveValue(utxoSelectionParameters.MiningFeeRate,
                        utxoSelectionParameters.CoordinationFeeRate))
                .ToList(), liquidityClue), 10);
        var remainingPendingPayments = new List<PendingPayment>(pendingPayments);
        var solution = new SubsetSolution(remainingPendingPayments.Count, _wallet.AnonScoreTarget,
            utxoSelectionParameters);

        var cv = new CoinsView(remainingCoins);
        var percentage = await _wallet.GetPrivacyPercentageAsync(cv);
        var fullyPrivate = await _wallet.IsWalletPrivateAsync(new CoinsView(remainingCoins));
        var coinjoiningOnlyForPayments = fullyPrivate && remainingPendingPayments.Any();

        if (!consolidationMode && percentage < 1 && _wallet.ConsolidationMode != ConsolidationModeType.Never)
        {
            consolidationMode = true;    
        }
        solution.ConsolidationMode = consolidationMode;
        if(consolidationMode && coins.Count() < 8 && !coinjoiningOnlyForPayments  && ineligibleCoins.Any())
        {
            //if we're in consolidation mode, and there are coins not eligible for a reason  that will be ok in the near future, we should try to wait for them to become eligible instead of entering multiple coinjoins, which costs more.
            // why are they ineligible? banned if not too far in the future is ok, unconfrmed as well
            if (ineligibleCoins.Any(coin => !coin.Confirmed))
            {
                // if there are unconfirmed coins, we should wait for them to confirm, but since we cant determine if they will be unconfirmed for a long time,. let's play  a random chance game: the more coins we have towards the 8 coin goal, the bigger the chance we proceed with the coinjoin
                var rand = Random.Shared.Next(1, 101);
                var chance = (coins.Count()/8) * 100;
                _wallet.LogDebug($"coin selection: consolidation mode, and there are coins not eligible for a reason  that will be ok in the near future, we should try to wait for them to become eligible instead of entering multiple coinjoins, which costs more. random chance to proceed: {chance} > {rand} (random 0-100) continue: {chance > rand}");
                if (chance > rand)
                {
                    return solution;
                }
            }
        }
        
        if (fullyPrivate && !coinjoiningOnlyForPayments )
        {
            var rand = Random.Shared.Next(1, 1001);
            if (rand > _wallet.WabisabiStoreSettings.ExtraJoinProbability)
            {
                _wallet.LogTrace($"All coins are private and we have no pending payments. Skipping join.");
                return solution;
            }

            _wallet.LogTrace(
                "All coins are private and we have no pending payments but will join just to reduce timing analysis");
        }

        while (remainingCoins.Any())
        {
            
            remainingCoins = remainingCoins.Where(coin => !coin.CoinJoinInProgress).ToList();
            if (!remainingCoins.Any())
            {
                break;
            }
            var coinColorCount = solution.SortedCoins.ToDictionary(pair => pair.Key, pair => pair.Value.Length);

            var predicate = new Func<SmartCoin, bool>(_ => true);
            foreach (var coinColor in idealMinimumPerType.ToShuffled(random))
            {
                if (coinColor.Value != 0)
                {
                    coinColorCount.TryGetValue(coinColor.Key, out var currentCoinColorCount);
                    if (currentCoinColorCount < coinColor.Value)
                    {
                        predicate = coin1 => coin1.CoinColor(_wallet.AnonScoreTarget) == coinColor.Key;
                        break;
                    }
                }
                else
                {
                    //if the ideal amount = 0, then we should de-prioritize.
                    predicate = coin1 => coin1.CoinColor(_wallet.AnonScoreTarget) != coinColor.Key;
                    break;
                }
            }

            var coin = remainingCoins.FirstOrDefault(predicate) ?? remainingCoins.First();
            var color = coin.CoinColor(_wallet.AnonScoreTarget);
            // If the selected coins list is at its maximum size, break out of the loop
            if (solution.Coins.Count == maxCoins)
            {
                break;
            }

            remainingCoins.Remove(coin);
            if (maxPerType.TryGetValue(color, out var maxColor) &&
                solution.Coins.Count(coin1 => coin1.CoinColor(_wallet.AnonScoreTarget) == color) == maxColor)
            {
                continue;
            }

            solution.Coins.Add(coin);
            // we make sure to spend all coins of the same script as it reduces the chance of the user stupidly consolidating later on
            var scriptPubKey = coin.ScriptPubKey;
            var reusedAddressCoins = remainingCoins.Where(smartCoin => smartCoin.ScriptPubKey == scriptPubKey).ToArray();
            foreach (var reusedAddressCoin in reusedAddressCoins)
            {
                remainingCoins.Remove(reusedAddressCoin);
                solution.Coins.Add(reusedAddressCoin);
            }

            // Loop through the pending payments and handle each payment by subtracting the payment amount from the total value of the selected coins
            var potentialPayments = remainingPendingPayments
                .Where(payment =>
                    payment.ToTxOut().EffectiveCost(utxoSelectionParameters.MiningFeeRate).ToDecimal(MoneyUnit.BTC) <=
                    solution.LeftoverValue).ToShuffled(random);

            while (potentialPayments.Any())
            {
                var payment = potentialPayments.First();
                solution.HandledPayments.Add(payment);
                remainingPendingPayments.Remove(payment);
                potentialPayments = remainingPendingPayments.Where(payment =>
                    payment.ToTxOut().EffectiveCost(utxoSelectionParameters.MiningFeeRate).ToDecimal(MoneyUnit.BTC) <=
                    solution.LeftoverValue).ToShuffled(random);
            }

            if (!remainingPendingPayments.Any())
            {
                //if we're in consolidation mode, we should use more than one coin at the very least
                if (solution.Coins.Count == 1 && consolidationMode)
                {
                    continue;
                }

             
                //if we have less than the max suggested output registration, we should add more coins to reach that number to avoid breaking up into too many coins?
                var isLessThanMaxOutputRegistration = solution.Coins.Count < Math.Max(solution.HandledPayments.Count +1, 8);
                var rand = Random.Shared.Next(1, 101);
                //let's check how many coins we are allowed to add max and how many we added, and use that percentage as the random chance of not adding it.
                // if max coins = 20, and current coins  = 5 then 5/20 = 0.25 * 100 = 25
                var maxCoinCapacityPercentage = Math.Floor((solution.Coins.Count / (decimal)maxCoins) * 100);
                //aggressively attempt to reach max coin target if consolidation mode is on
                //if we're less than the max output registration, we should be more aggressive in adding coins

                decimal chance = 100;
                if (consolidationMode && !isLessThanMaxOutputRegistration)
                {
                    chance -= maxCoinCapacityPercentage / random.GetInt(2, 8);
                }
                else if (!isLessThanMaxOutputRegistration)
                {
                    chance -= maxCoinCapacityPercentage;
                }

               
                if (chance <= rand)
                {
                    if (_wallet.MinimumDenominationAmount is not null &&
                        Money.Coins(solution.LeftoverValue).Satoshi < _wallet.MinimumDenominationAmount)
                    {
                        _wallet.LogDebug(
                            $"coin selection: leftover value {solution.LeftoverValue} is less than minimum denomination amount {_wallet.MinimumDenominationAmount} so we will try to add more coins");
                        continue;
                    }
                    _wallet.LogDebug($"coin selection: no payments left but at {solution.Coins.Count()} coins. random chance to add another coin if: {chance} > {rand} (random 0-100) continue: {chance > rand}");
                    break;
                }
                
            }
        }

        if (coinjoiningOnlyForPayments && solution.HandledPayments?.Any() is not true)
        {
            _wallet.LogInfo(
                "Attempted to coinjoin only to fulfill payments but the coin selection results yielded no handled payment.");
            return  new SubsetSolution(remainingPendingPayments.Count, _wallet.AnonScoreTarget,
                utxoSelectionParameters);
        }
        return solution;
    }

    static List<T> SlightlyShiftOrder<T>(List<T> list, int chanceOfShiftPercentage)
    {
        // Create a random number generator
        var rand = new Random();
        List<T> workingList = new List<T>(list);
// Loop through the coins and determine whether to swap the positions of two consecutive coins in the list
        for (int i = 0; i < workingList.Count() - 1; i++)
        {
            // If a random number between 0 and 1 is less than or equal to 0.1, swap the positions of the current and next coins in the list
            if (rand.NextDouble() <= ((double)chanceOfShiftPercentage / 100))
            {
                // Swap the positions of the current and next coins in the list
                (workingList[i], workingList[i + 1]) = (workingList[i + 1], workingList[i]);
            }
        }

        return workingList;
    }

    private List<SmartCoin> RandomizeCoins(List<SmartCoin> coins, Money liquidityClue)
    {
        var remainingCoins = new List<SmartCoin>(coins);
        var workingList = new List<SmartCoin>();
        while (remainingCoins.Any())
        {
            var currentCoin = remainingCoins.First();
            remainingCoins.RemoveAt(0);
            var lastCoin = workingList.LastOrDefault();
            if (lastCoin is null || currentCoin.CoinColor(_wallet.AnonScoreTarget) == AnonsetType.Green ||
                !remainingCoins.Any() ||
                (remainingCoins.Count == 1 && remainingCoins.First().TransactionId == currentCoin.TransactionId) ||
                lastCoin.TransactionId != currentCoin.TransactionId ||
                liquidityClue <= currentCoin.Amount ||
                Random.Shared.Next(0, 10) < 5)
            {
                workingList.Add(currentCoin);
            }
            else
            {
                remainingCoins.Insert(1, currentCoin);
            }
        }


        return workingList.ToList();
    }
}

public static class SmartCoinExtensions
{
    public static AnonsetType CoinColor(this SmartCoin coin, int anonsetTarget)
    {
        return coin.IsPrivate(anonsetTarget)? AnonsetType.Green: coin.IsSemiPrivate(anonsetTarget)? AnonsetType.Orange: AnonsetType.Red;
    }
}

public enum AnonsetType
{
    Red,
    Orange,
    Green
}

public class SubsetSolution
{
    private readonly UtxoSelectionParameters _utxoSelectionParameters;

    public SubsetSolution(int totalPaymentsGross, int anonsetTarget, UtxoSelectionParameters utxoSelectionParameters)
    {
        _utxoSelectionParameters = utxoSelectionParameters;
        TotalPaymentsGross = totalPaymentsGross;
        AnonsetTarget = anonsetTarget;
    }
    public List<SmartCoin> Coins { get; set; } = new();
    public List<PendingPayment> HandledPayments { get; set; } = new();

    public decimal TotalValue => Coins.Sum(coin =>
        coin.EffectiveValue(_utxoSelectionParameters.MiningFeeRate, _utxoSelectionParameters.CoordinationFeeRate)
            .ToDecimal(MoneyUnit.BTC));

    public Dictionary<AnonsetType, SmartCoin[]> SortedCoins =>
        Coins.GroupBy(coin => coin.CoinColor(AnonsetTarget)).ToDictionary(coins => coins.Key, coins => coins.ToArray());

    public int TotalPaymentsGross { get; }
    public int AnonsetTarget { get; }

    public decimal TotalPaymentCost => HandledPayments.Sum(payment =>
        payment.ToTxOut().EffectiveCost(_utxoSelectionParameters.MiningFeeRate).ToDecimal(MoneyUnit.BTC));

    public decimal LeftoverValue => TotalValue - TotalPaymentCost;
    public bool ConsolidationMode { get; set; }

    public override string ToString()
    {
        if (!Coins.Any())
        {
            return "Solution yielded no selection of coins";
        }

        var sc = SortedCoins;
        sc.TryGetValue(AnonsetType.Green, out var gcoins);
        sc.TryGetValue(AnonsetType.Orange, out var ocoins);
        sc.TryGetValue(AnonsetType.Red, out var rcoins);

        
        return $"Selected {Coins.Count} ({TotalValue} BTC) ({ocoins?.Length +  rcoins?.Length} not private, {gcoins?.Length ?? 0} private) coins to pay {TotalPaymentsGross} payments ({TotalPaymentCost} BTC) with {LeftoverValue} BTC leftover\n Consolidation mode:{ConsolidationMode}";
    }
}
