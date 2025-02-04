﻿using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Core;
using WalletWasabi.Affiliation;
using WalletWasabi.Affiliation.Models;
using WalletWasabi.Affiliation.Models.CoinJoinNotification;
using WalletWasabi.Affiliation.Serialization;
using Constants = WalletWasabi.Helpers.Constants;

namespace BTCPayServer.Plugins.Wabisabi.AffiliateServer;

public class WabisabiAffiliateSettings
{
    public bool Enabled { get; set; }
    public string SigningKey { get; set; }
    
    
}
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/wabisabi-affiliate")]
public class AffiliateServerController:Controller
{
    private readonly SettingsRepository _settingsRepository;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly ILogger<AffiliateServerController> _logger;

    public AffiliateServerController(SettingsRepository settingsRepository, IOptions<DataDirectories> dataDirectories, ILogger<AffiliateServerController> logger)
    {
        _settingsRepository = settingsRepository;
        _dataDirectories = dataDirectories;
        _logger = logger;
    }

    [HttpGet("edit")]
    public async Task<IActionResult> Edit()
    {
        var settings = 
            await _settingsRepository.GetSettingAsync<WabisabiAffiliateSettings>();
        return View(settings);
    }
    [HttpPost("edit")]
    public async Task<IActionResult> Edit(WabisabiAffiliateSettings settings)
    {
        await _settingsRepository.UpdateSetting(settings);
        return RedirectToAction("Edit");
    }
    [HttpGet("history")]
    public async Task<IActionResult> ViewRequests()
    {
        
        var path = Path.Combine(_dataDirectories.Value.DataDir, "Plugins", "CoinjoinAffiliate",  $"History{DateTime.Today:dd_MM_yyyy}.txt");
        if (!System.IO.File.Exists(path))
            return NotFound();

        return PhysicalFile(path,  MediaTypeNames.Text.Plain);
    }
    

    [AllowAnonymous]
    [HttpPost("get_status")]
    [HttpGet("get_status")]
    public async Task<IActionResult> GetStatus()
    {
        _logger.LogTrace("GetStatus Called");
        var settings = 
            await _settingsRepository.GetSettingAsync<WabisabiAffiliateSettings>();
        if(settings?.Enabled is true&& !string.IsNullOrEmpty(settings.SigningKey))
            return Ok(new { });
        return NotFound();

    }

    private static ECDsa ecdsa = ECDsa.Create();
    
    
    [AllowAnonymous]
    [HttpPost("notify_coinjoin")]
    [HttpGet("notify_coinjoin")]
    public async Task<IActionResult> GetCoinjoinRequest()
    {
        
        _logger.LogTrace("notify_coinjoin Called");
        var settings = await _settingsRepository.GetSettingAsync<WabisabiAffiliateSettings>();
        if (settings?.Enabled is not true)
        {
            return NotFound();
        }
        var keyB = Encoders.Hex.DecodeData(settings.SigningKey);
        ecdsa.ImportSubjectPublicKeyInfo(keyB.AsSpan(), out _);

        using var reader = new StreamReader(Request.Body);
        var request =
            AffiliateServerHttpApiClient.Deserialize<CoinJoinNotificationRequest>(await reader.ReadToEndAsync());
        if (request.Body.Inputs.All(input => !input.IsAffiliated))
        {
            var response = new CoinJoinNotificationResponse(Array.Empty<byte>());
            return Json(response, AffiliationJsonSerializationOptions.Settings);
        }

        Payload payload = new(Header.Instance,  request.Body);
        try
        {

            var valid = ecdsa.VerifyData(payload.GetCanonicalSerialization(), request.Signature, HashAlgorithmName.SHA256);
            if (!valid)
            {
                
                _logger.LogError($"Invalid coinjoin request sent\n{payload.GetCanonicalSerialization()}\n{request.Signature}" );
                return NotFound();
            }
            var path = Path.Combine(_dataDirectories.Value.DataDir, "Plugins", "CoinjoinAffiliate", $"History{DateTime.Today:dd_MM_yyyy}.txt");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await System.IO.File.AppendAllLinesAsync(path, new[] {JObject.FromObject(request, JsonSerializer.Create(AffiliationJsonSerializationOptions.Settings) ).ToString(Formatting.None).Replace(Environment.NewLine, "")}, Encoding.UTF8);
            var response = new CoinJoinNotificationResponse(Array.Empty<byte>());
            return Json(response, AffiliationJsonSerializationOptions.Settings);

        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed on GetCoinjoinRequest" );
            return NotFound();

        }
        
    }
    
    
}