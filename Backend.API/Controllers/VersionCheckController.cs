using System;
using Core.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/system")]
public class VersionCheckController : ControllerBase
{
    private readonly IOptionsMonitor<SystemSettingsOptions> _systemSettingsMonitor;

    public VersionCheckController(IOptionsMonitor<SystemSettingsOptions> systemSettingsMonitor)
    {
        _systemSettingsMonitor = systemSettingsMonitor;
    }

    [HttpGet("version-check")]
    public IActionResult CheckVersion([FromHeader(Name = "X-Client-Version")] string? clientVersion)
    {
        var settings = _systemSettingsMonitor.CurrentValue;

        bool isCompatible = true;
        if (!string.IsNullOrWhiteSpace(clientVersion) &&
            Version.TryParse(clientVersion, out var clientVer) &&
            Version.TryParse(settings.MinimumClientVersion, out var minVer))
        {
            isCompatible = clientVer >= minVer;
        }

        return Ok(new
        {
            serverVersion = settings.ServerVersion,
            minimumClientVersion = settings.MinimumClientVersion,
            updateServerUrl = settings.UpdateServerUrl,
            clientVersionReceived = clientVersion ?? "0.0.0",
            isClientCompatible = isCompatible
        });
    }
}
