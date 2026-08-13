using System.Threading.Tasks;

namespace Core.Interfaces;

public interface ISystemSettingsService
{
    Task<string?> GetSettingAsync(string key);
    Task SetSettingAsync(string key, string value);
}
