using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
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

public partial class PairingQrViewModel : ObservableObject, IDisposable
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
    private bool _useHttps = true;

    [ObservableProperty]
    private string _fullUrl = "https://127.0.0.1:5001";

    [ObservableProperty]
    private string _qrPayload = "https://127.0.0.1:5001/?paired=true";

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
        ArgumentNullException.ThrowIfNull(httpClient);
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
                IpAddress = "127.0.0.1";
                UpdateUrls();
                StatusMessage = "No se pudo obtener la configuración de red remota.";
            }
        }
        catch
        {
            IpAddress = "127.0.0.1";
            UpdateUrls();
            StatusMessage = "No se pudo obtener la configuración de red remota.";
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
    private int _activePort = 5001;

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

    [ObservableProperty]
    private bool _isIpCopied;

    [ObservableProperty]
    private bool _isUrlCopied;

    private CancellationTokenSource? _copyIpCts;
    private CancellationTokenSource? _copyUrlCts;

    [RelayCommand]
    private async Task CopyIpAsync()
    {
        RequestClipboardCopy?.Invoke(IpAddress);
        IsIpCopied = true;
        _copyIpCts?.Cancel();
        _copyIpCts?.Dispose();
        _copyIpCts = new CancellationTokenSource();
        var token = _copyIpCts.Token;
        try
        {
            await Task.Delay(2000, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsIpCopied = false;
            }
        }
    }

    [RelayCommand]
    private async Task CopyUrlAsync()
    {
        RequestClipboardCopy?.Invoke(FullUrl);
        IsUrlCopied = true;
        _copyUrlCts?.Cancel();
        _copyUrlCts?.Dispose();
        _copyUrlCts = new CancellationTokenSource();
        var token = _copyUrlCts.Token;
        try
        {
            await Task.Delay(2000, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsUrlCopied = false;
            }
        }
    }

    public void Dispose()
    {
        _copyIpCts?.Cancel();
        _copyIpCts?.Dispose();
        _copyIpCts = null;

        _copyUrlCts?.Cancel();
        _copyUrlCts?.Dispose();
        _copyUrlCts = null;

        GC.SuppressFinalize(this);
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
