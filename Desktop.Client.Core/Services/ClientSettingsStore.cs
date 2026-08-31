using System;
using System.IO;
using System.Text.Json;

namespace Desktop.Client.Services;

public class ClientAppSettings
{
    public string ServerBaseAddress { get; set; } = "http://localhost:5000/";
    public string? LastKnownServerIp { get; set; }
    public string? LastKnownServerMachineName { get; set; }
    public bool AutoDiscoverOnFailure { get; set; } = true;
    public DateTime? LastUpdatedUtc { get; set; }
}

public interface IClientSettingsStore
{
    ClientAppSettings LoadSettings();
    void SaveSettings(ClientAppSettings settings);
    void UpdateServerAddress(string newAddress, string? machineName = null);
}

public class ClientSettingsStore : IClientSettingsStore
{
    private readonly string _settingsFilePath;
    private readonly object _lock = new object();
    private ClientAppSettings? _cachedSettings;

    public ClientSettingsStore(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _settingsFilePath = customPath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appData, "ProyectoPOS");
            Directory.CreateDirectory(folder);
            _settingsFilePath = Path.Combine(folder, "client_settings.json");
        }
    }

    public ClientAppSettings LoadSettings()
    {
        lock (_lock)
        {
            if (_cachedSettings != null)
                return _cachedSettings;

            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<ClientAppSettings>(json);
                    if (settings != null)
                    {
                        if (!settings.ServerBaseAddress.EndsWith("/"))
                            settings.ServerBaseAddress += "/";

                        _cachedSettings = settings;
                        return settings;
                    }
                }
                catch
                {
                    // Fallback si el archivo está corrupto
                }
            }

            _cachedSettings = new ClientAppSettings();
            return _cachedSettings;
        }
    }

    public void SaveSettings(ClientAppSettings settings)
    {
        lock (_lock)
        {
            if (!settings.ServerBaseAddress.EndsWith("/"))
                settings.ServerBaseAddress += "/";

            settings.LastUpdatedUtc = DateTime.UtcNow;
            _cachedSettings = settings;

            try
            {
                var dir = Path.GetDirectoryName(_settingsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch
            {
                // Manejar silenciosamente en caso de restricciones transitorias de disco
            }
        }
    }

    public void UpdateServerAddress(string newAddress, string? machineName = null)
    {
        var settings = LoadSettings();
        settings.ServerBaseAddress = newAddress;
        if (!string.IsNullOrWhiteSpace(machineName))
        {
            settings.LastKnownServerMachineName = machineName;
        }

        // Si es una IP, extraerla para LastKnownServerIp
        if (Uri.TryCreate(newAddress, UriKind.Absolute, out var uri))
        {
            settings.LastKnownServerIp = uri.Host;
        }

        SaveSettings(settings);
    }
}
