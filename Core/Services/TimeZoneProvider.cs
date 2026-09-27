using Core.Helpers;
using Core.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Services;

public class TimeZoneProvider : ITimeZoneProvider
{
    private readonly ISystemSettingsService _settingsService;
    private TimeZoneInfo? _cachedTimeZoneInfo;
    private string? _cachedTimeZoneId;

    public TimeZoneProvider(ISystemSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<TimeZoneInfo> GetTimeZoneInfoAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedTimeZoneInfo != null)
        {
            return _cachedTimeZoneInfo;
        }

        var tzId = await GetTimeZoneIdAsync(cancellationToken);
        _cachedTimeZoneInfo = TimeZoneHelper.GetTimeZone(tzId);
        return _cachedTimeZoneInfo;
    }

    public async Task<string> GetTimeZoneIdAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedTimeZoneId != null)
        {
            return _cachedTimeZoneId;
        }

        var tzId = await _settingsService.GetSettingAsync("SelectedTimeZoneId");
        _cachedTimeZoneId = tzId ?? string.Empty;
        return _cachedTimeZoneId;
    }

    public void Invalidate()
    {
        _cachedTimeZoneInfo = null;
        _cachedTimeZoneId = null;
    }
}
