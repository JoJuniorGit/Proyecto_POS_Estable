using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public class NetworkInterfaceItem
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string InterfaceType { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }

    public string DisplayName => $"{InterfaceType}: {IpAddress} ({Name})";
}

public partial class PairingQrViewModel : ObservableObject
{
    private readonly HttpClient _httpClient;

    [ObservableProperty]
    private string _serverName = Environment.MachineName;

    [ObservableProperty]
    private string _machineName = Environment.MachineName;

    [ObservableProperty]
    private string _ipAddress = "127.0.0.1";

    [ObservableProperty]
    private int _httpPort = 5000;

    [ObservableProperty]
    private int _httpsPort = 5001;

    [ObservableProperty]
    private bool _useHttps;

    [ObservableProperty]
    private string _fullUrl = "http://localhost:5000";

    [ObservableProperty]
    private string _qrPayload = "http://localhost:5000/?paired=true";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private ObservableCollection<NetworkInterfaceItem> _availableInterfaces = new();

    [ObservableProperty]
    private NetworkInterfaceItem? _selectedInterface;

    public event Action<string>? RequestClipboardCopy;

    public PairingQrViewModel(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        StatusMessage = "Obteniendo información de red...";

        try
        {
            var response = await _httpClient.GetAsync("api/pairing/info");
            if (response.IsSuccessStatusCode)
            {
                var info = await response.Content.ReadFromJsonAsync<PairingApiResponse>();
                if (info != null)
                {
                    ServerName = info.ServerName;
                    MachineName = info.MachineName;
                    HttpPort = info.HttpPort;
                    HttpsPort = info.HttpsPort;

                    AvailableInterfaces.Clear();
                    if (info.NetworkInterfaces != null)
                    {
                        foreach (var iface in info.NetworkInterfaces)
                        {
                            AvailableInterfaces.Add(new NetworkInterfaceItem
                            {
                                Name = iface.Name,
                                Description = iface.Description,
                                IpAddress = iface.IpAddress,
                                InterfaceType = iface.InterfaceType,
                                IsPrimary = iface.IsPrimary
                            });
                        }
                    }

                    var primary = AvailableInterfaces.FirstOrDefault(i => i.IsPrimary) 
                                  ?? AvailableInterfaces.FirstOrDefault();

                    if (primary != null)
                    {
                        SelectedInterface = primary;
                    }
                    else
                    {
                        IpAddress = info.PrimaryIpAddress ?? "127.0.0.1";
                        UpdateUrls();
                    }

                    StatusMessage = string.Empty;
                }
            }
            else
            {
                // Fallback si la API no está accesible vía endpoint o no tiene permisos
                IpAddress = "127.0.0.1";
                UpdateUrls();
                StatusMessage = "No se pudo obtener la configuración de red remota.";
            }
        }
        catch (Exception ex)
        {
            IpAddress = "127.0.0.1";
            UpdateUrls();
            StatusMessage = $"Aviso: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedInterfaceChanged(NetworkInterfaceItem? value)
    {
        if (value != null)
        {
            IpAddress = value.IpAddress;
            UpdateUrls();
        }
    }

    [ObservableProperty]
    private int _activePort = 5000;

    partial void OnUseHttpsChanged(bool value)
    {
        UpdateUrls();
    }

    private void UpdateUrls()
    {
        var scheme = UseHttps ? "https" : "http";
        var port = UseHttps ? HttpsPort : HttpPort;
        ActivePort = port;
        FullUrl = $"{scheme}://{IpAddress}:{port}";
        QrPayload = $"{FullUrl}/?paired=true";
    }

    [RelayCommand]
    private void CopyIp()
    {
        RequestClipboardCopy?.Invoke(IpAddress);
    }

    [RelayCommand]
    private void CopyUrl()
    {
        RequestClipboardCopy?.Invoke(FullUrl);
    }

    private class PairingApiResponse
    {
        public string ServerName { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public string PrimaryIpAddress { get; set; } = string.Empty;
        public int HttpPort { get; set; } = 5000;
        public int HttpsPort { get; set; } = 5001;
        public NetworkInterfaceItem[]? NetworkInterfaces { get; set; }
    }
}
