using System;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface ITimeZoneProvider
{
    Task<TimeZoneInfo> GetTimeZoneInfoAsync(CancellationToken cancellationToken = default);
    Task<string> GetTimeZoneIdAsync(CancellationToken cancellationToken = default);
    void Invalidate();
}
