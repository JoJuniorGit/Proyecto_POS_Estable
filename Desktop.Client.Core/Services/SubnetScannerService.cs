using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class DiscoveredServer
{
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 5000;
    public string BaseUrl { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsHealthy { get; set; }
    public long ResponseTimeMs { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(MachineName) 
        ? $"Servidor POS ({IpAddress})" 
        : $"{MachineName} ({IpAddress})";
}

public interface ISubnetScannerService
{
    Task<DiscoveredServer?> ProbeSingleHostAsync(string hostOrIp, int port = 5000, int timeoutMs = 600, CancellationToken ct = default);
    Task<List<DiscoveredServer>> ScanSubnetAsync(IProgress<int>? progress = null, bool force = false, CancellationToken ct = default);
    Task<DiscoveredServer?> QuickDiscoverAsync(string? preferredHostOrIp = null, CancellationToken ct = default);
}

public class SubnetScannerService : ISubnetScannerService
{
    private static readonly HttpClient SharedScannerClient = new HttpClient(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
        }
    })
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private static readonly string[] VirtualKeywords = new[]
    {
        "virtual", "hyper-v", "wsl", "docker", "vmware", "vethernet",
        "default switch", "bluetooth", "npcap", "tailscale", "zerotier",
        "wireguard", "vpn", "loopback"
    };

    private DateTime _lastScanUtc = DateTime.MinValue;
    private List<DiscoveredServer> _lastScanResults = new();
    private readonly object _lock = new object();

    public async Task<DiscoveredServer?> ProbeSingleHostAsync(string hostOrIp, int port = 5000, int timeoutMs = 1500, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostOrIp)) return null;

        var raw = hostOrIp.Trim();
        var scheme = "http";
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "https";
            raw = raw.Substring(8);
        }
        else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "http";
            raw = raw.Substring(7);
        }

        int targetPort = port;
        string cleanHost = raw;
        if (cleanHost.Contains("/"))
        {
            cleanHost = cleanHost.Substring(0, cleanHost.IndexOf('/'));
        }

        if (cleanHost.Contains(":"))
        {
            var parts = cleanHost.Split(':');
            cleanHost = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out int parsedPort))
            {
                targetPort = parsedPort;
            }
        }

        var targetUrl = $"{scheme}://{cleanHost}:{targetPort}/api/health";
        var sw = Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            using var response = await SharedScannerClient.GetAsync(targetUrl, cts.Token).ConfigureAwait(false);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var machine = cleanHost;
                var version = "1.0.0";
                var status = "Healthy";
                var service = "Proyecto_POS_Server";

                if (!string.IsNullOrWhiteSpace(content))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(content);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("machineName", out var mElem)) machine = mElem.GetString() ?? cleanHost;
                        if (root.TryGetProperty("service", out var srvElem)) service = srvElem.GetString() ?? "Proyecto_POS_Server";
                        if (root.TryGetProperty("version", out var vElem)) version = vElem.GetString() ?? "1.0.0";
                        if (root.TryGetProperty("status", out var sElem)) status = sElem.GetString() ?? "Healthy";
                    }
                    catch { }
                }

                return new DiscoveredServer
                {
                    IpAddress = cleanHost,
                    Port = targetPort,
                    BaseUrl = $"{scheme}://{cleanHost}:{targetPort}/",
                    MachineName = machine,
                    Service = service,
                    Version = version,
                    IsHealthy = status.Equals("Healthy", StringComparison.OrdinalIgnoreCase),
                    ResponseTimeMs = sw.ElapsedMilliseconds
                };
            }
        }
        catch
        {
            // Host unreachable or timeout
        }

        return null;
    }

    public async Task<DiscoveredServer?> QuickDiscoverAsync(string? preferredHostOrIp = null, CancellationToken ct = default)
    {
        // Paso 0: Probar IP preferida / anterior si existe
        if (!string.IsNullOrWhiteSpace(preferredHostOrIp))
        {
            var cachedProbe = await ProbeSingleHostAsync(preferredHostOrIp, port: 5000, timeoutMs: 400, ct).ConfigureAwait(false);
            if (cachedProbe != null)
                return cachedProbe;
        }

        // Paso 1: Probar localhost
        var localProbe = await ProbeSingleHostAsync("127.0.0.1", port: 5000, timeoutMs: 400, ct).ConfigureAwait(false);
        if (localProbe != null)
            return localProbe;

        // Paso 2: Escanear subred
        var discovered = await ScanSubnetAsync(force: false, ct: ct).ConfigureAwait(false);
        return discovered.FirstOrDefault();
    }

    public async Task<List<DiscoveredServer>> ScanSubnetAsync(IProgress<int>? progress = null, bool force = false, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!force && DateTime.UtcNow - _lastScanUtc < TimeSpan.FromSeconds(60) && _lastScanResults.Any())
            {
                progress?.Report(100);
                return _lastScanResults.ToList();
            }
        }

        var candidateIps = GetLocalSubnetCandidateIps();
        if (!candidateIps.Any())
        {
            progress?.Report(100);
            return new List<DiscoveredServer>();
        }

        var foundServers = new ConcurrentBag<DiscoveredServer>();
        int total = candidateIps.Count;
        int completed = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 32,
            CancellationToken = ct
        };

        try
        {
            await Parallel.ForEachAsync(candidateIps, parallelOptions, async (ip, token) =>
            {
                var result = await ProbeSingleHostAsync(ip, port: 5000, timeoutMs: 500, token).ConfigureAwait(false);
                if (result != null)
                {
                    foundServers.Add(result);
                }

                var count = Interlocked.Increment(ref completed);
                var pct = (int)((count * 100.0) / total);
                progress?.Report(pct);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        var results = foundServers.OrderBy(s => s.ResponseTimeMs).ToList();
        lock (_lock)
        {
            _lastScanUtc = DateTime.UtcNow;
            _lastScanResults = results;
        }

        progress?.Report(100);
        return results;
    }

    private List<string> GetLocalSubnetCandidateIps()
    {
        var ips = new List<string>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var adapter in interfaces)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var name = adapter.Name.ToLowerInvariant();
                var desc = adapter.Description.ToLowerInvariant();

                if (VirtualKeywords.Any(k => name.Contains(k) || desc.Contains(k)))
                    continue;

                var ipProps = adapter.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = unicast.Address.ToString();
                        if (ip.StartsWith("127.") || ip.StartsWith("169.254."))
                            continue;

                        // Extraer prefijo de subred /24 estándar
                        var lastDot = ip.LastIndexOf('.');
                        if (lastDot > 0)
                        {
                            var subnetPrefix = ip.Substring(0, lastDot);
                            for (int i = 1; i <= 254; i++)
                            {
                                ips.Add($"{subnetPrefix}.{i}");
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return ips.Distinct().ToList();
    }
}
