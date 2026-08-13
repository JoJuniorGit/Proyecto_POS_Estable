using System.Threading.Tasks;

namespace Desktop.Client.Services;

public interface ISettingsService
{
    Task<string> GetTimeZoneAsync();
    Task SetTimeZoneAsync(string timeZoneId);
}
