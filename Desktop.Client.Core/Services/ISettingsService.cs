using System.Threading.Tasks;

namespace Desktop.Client.Services;

public interface ISettingsService
{
    Task<string> GetTimeZoneAsync();
    Task SetTimeZoneAsync(string timeZoneId);
Task<string> GetCurrencyFormatAsync();
Task SetCurrencyFormatAsync(string format);
Task<bool> GetAllowNegativeStockAsync();
Task SetAllowNegativeStockAsync(bool allowed);
}
