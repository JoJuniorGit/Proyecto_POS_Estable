using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class VersionCheckResult
{
    public bool IsCompatible { get; set; } = true;
    public string MinimumClientVersion { get; set; } = "1.0.0";
    public string ServerVersion { get; set; } = "1.0.0";
    public string UpdateServerUrl { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public interface IVersionCheckService
{
    Task<VersionCheckResult> CheckVersionAsync();
}

public class VersionCheckService : IVersionCheckService
{
    private readonly HttpClient _httpClient;

    public VersionCheckService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<VersionCheckResult> CheckVersionAsync()
    {
        var result = new VersionCheckResult();
        try
        {
            var response = await _httpClient.GetAsync("api/system/version-check");
            if (response.StatusCode == System.Net.HttpStatusCode.UpgradeRequired)
            {
                result.IsCompatible = false;
                result.ErrorMessage = "Su versión de cliente es obsoleta y el servidor requiere una actualización obligatoria.";
                return result;
            }

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (content.TryGetProperty("isClientCompatible", out var compatProp))
                {
                    result.IsCompatible = compatProp.GetBoolean();
                }
                if (content.TryGetProperty("minimumClientVersion", out var minVerProp))
                {
                    result.MinimumClientVersion = minVerProp.GetString() ?? "1.0.0";
                }
                if (content.TryGetProperty("serverVersion", out var srvVerProp))
                {
                    result.ServerVersion = srvVerProp.GetString() ?? "1.0.0";
                }
                if (content.TryGetProperty("updateServerUrl", out var updateUrlProp))
                {
                    result.UpdateServerUrl = updateUrlProp.GetString() ?? string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            // Network or server unreachable: allow startup or log
            Console.WriteLine("[VersionCheck] Unable to reach version check endpoint: " + ex.Message);
        }

        return result;
    }
}
