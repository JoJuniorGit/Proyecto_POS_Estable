using System.Collections.Generic;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface ISystemSettingsService
{
    Task<string?> GetSettingAsync(string key);
    /// <summary>8.211-T6-U10: reads a set of keys in one query so a profile snapshot cannot mix values.</summary>
    Task<IReadOnlyDictionary<string, string?>> GetSettingsAsync(IReadOnlyCollection<string> keys, System.Threading.CancellationToken cancellationToken = default);
    Task SetSettingAsync(string key, string value);

    /// <summary>
    /// 8.211-T1 (S2): upsert batch con un único SaveChangesAsync y posterior invalidación de caché
    /// por clave. Usado por el perfil monetario para que sus claves se persistan de forma atómica.
    /// </summary>
    Task SetSettingsAsync(IReadOnlyDictionary<string, string> values);
}


