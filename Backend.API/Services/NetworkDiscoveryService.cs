using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Backend.API.Services;

public class NetworkInterfaceInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string InterfaceType { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
}

public class ServerPairingInfo
{
    public string ServerName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string PrimaryIpAddress { get; set; } = string.Empty;
    public int HttpPort { get; set; } = 5000;
    public int HttpsPort { get; set; } = 5001;
    public string PrimaryHttpUrl { get; set; } = string.Empty;
    public string PrimaryHttpsUrl { get; set; } = string.Empty;
    public bool IsHttpsEnabled { get; set; }
    public List<NetworkInterfaceInfo> NetworkInterfaces { get; set; } = new();
    public string QrPayload { get; set; } = string.Empty;
}

public interface INetworkDiscoveryService
{
    ServerPairingInfo GetPairingInfo(int httpPort = 5000, int httpsPort = 5001, bool isHttpsEnabled = true);
    List<NetworkInterfaceInfo> GetPhysicalIPv4Interfaces();
    bool TryClaimPairingToken(string? token);
}

public class NetworkDiscoveryService : INetworkDiscoveryService
{
    private static readonly string[] VirtualKeywords = new[]
    {
        "virtual", "hyper-v", "wsl", "docker", "vmware", "vethernet",
        "default switch", "bluetooth", "npcap", "tailscale", "zerotier",
        "wireguard", "vpn", "loopback", "pseudo", "teredo", "isatap"
    };

    // 8.9-M4: token de emparejamiento efímero de un solo uso. El QR ya no es una URL estática:
    // incorpora un secreto aleatorio (192 bits) de corta vida y consumo único, de modo que un QR
    // fotografiado o capturado en la LAN no pueda reutilizarse para emparejar otro dispositivo.
    private const int PairingTokenTtlMinutes = 10;
    private const int PairingTokenKeyBytes = 24;
    private readonly ConcurrentDictionary<string, PairingTokenEntry> _pairingTokens = new();

    private sealed class PairingTokenEntry
    {
        public required DateTime CreatedUtc { get; init; }
        public bool Used { get; set; }
    }

    private static string CreatePairingToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(PairingTokenKeyBytes));

    private void PrunePairingTokens()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(PairingTokenTtlMinutes);
        foreach (var kvp in _pairingTokens)
        {
            if (kvp.Value.CreatedUtc < cutoff)
            {
                _pairingTokens.TryRemove(kvp.Key, out _);
            }
        }
    }

    private string GetOrCreatePairingToken()
    {
        PrunePairingTokens();
        var now = DateTime.UtcNow;

        // Reutilizar el token vigente y sin usar: si el cajero reabre el dialogo QR o el cliente
        // reintenta GET /info, el QR mostrado sigue siendo válido.
        foreach (var kvp in _pairingTokens)
        {
            if (!kvp.Value.Used && now - kvp.Value.CreatedUtc <= TimeSpan.FromMinutes(PairingTokenTtlMinutes))
            {
                return kvp.Key;
            }
        }

        var token = CreatePairingToken();
        _pairingTokens[token] = new PairingTokenEntry { CreatedUtc = now };
        return token;
    }

    public bool TryClaimPairingToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        PrunePairingTokens();
        if (!_pairingTokens.TryGetValue(token, out var entry)) return false;

        if (DateTime.UtcNow - entry.CreatedUtc > TimeSpan.FromMinutes(PairingTokenTtlMinutes)) return false;

        // Consumo único (single-use): solo un dispositivo puede reclamar el token.
        if (entry.Used) return false;
        if (_pairingTokens.TryUpdate(token, new PairingTokenEntry { CreatedUtc = entry.CreatedUtc, Used = true }, entry))
        {
            return true;
        }

        // Carrera perdida: otro reclamante consumió el token primero.
        return false;
    }

    public ServerPairingInfo GetPairingInfo(int httpPort = 5000, int httpsPort = 5001, bool isHttpsEnabled = true)
    {
        var machineName = Environment.MachineName;
        var interfaces = GetPhysicalIPv4Interfaces();

        // Elegir la interfaz primaria: preferir Wi-Fi o Ethernet activa con IP privada RFC 1918
        var primary = interfaces.FirstOrDefault(i => i.IsPrimary) 
                      ?? interfaces.FirstOrDefault() 
                      ?? new NetworkInterfaceInfo
                      {
                          Name = "Loopback",
                          Description = "Localhost",
                          IpAddress = "127.0.0.1",
                          InterfaceType = "Loopback",
                          IsPrimary = true
                      };

        var primaryIp = primary.IpAddress;
        var httpUrl = $"http://{primaryIp}:{httpPort}";
        var httpsUrl = isHttpsEnabled ? $"https://{primaryIp}:{httpsPort}" : string.Empty;
        var effectiveUrl = isHttpsEnabled ? httpsUrl : httpUrl;

        // 8.9-M4: el QR incorpora el secreto efímero de emparejamiento (single-use).
        var pairingToken = GetOrCreatePairingToken();

        return new ServerPairingInfo
        {
            ServerName = machineName,
            MachineName = machineName,
            PrimaryIpAddress = primaryIp,
            HttpPort = httpPort,
            HttpsPort = httpsPort,
            PrimaryHttpUrl = httpUrl,
            PrimaryHttpsUrl = httpsUrl,
            IsHttpsEnabled = isHttpsEnabled,
            NetworkInterfaces = interfaces,
            QrPayload = $"{effectiveUrl}/?paired=true&pair={pairingToken}"
        };
    }

    public List<NetworkInterfaceInfo> GetPhysicalIPv4Interfaces()
    {
        var results = new List<NetworkInterfaceInfo>();

        try
        {
            var allInterfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var adapter in allInterfaces)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var nameLower = adapter.Name.ToLowerInvariant();
                var descLower = adapter.Description.ToLowerInvariant();

                // Filtrar adaptadores virtuales
                if (VirtualKeywords.Any(k => nameLower.Contains(k) || descLower.Contains(k)))
                    continue;

                var ipProps = adapter.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = unicast.Address.ToString();

                        // Ignorar loopback y APIPA (169.254.x.x)
                        if (ip.StartsWith("127.") || ip.StartsWith("169.254."))
                            continue;

                        // Verificar que sea una IP privada válida (RFC 1918)
                        if (IsPrivateIPv4(unicast.Address))
                        {
                            var typeStr = adapter.NetworkInterfaceType switch
                            {
                                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                                NetworkInterfaceType.Ethernet => "Ethernet",
                                _ => adapter.NetworkInterfaceType.ToString()
                            };

                            results.Add(new NetworkInterfaceInfo
                            {
                                Name = adapter.Name,
                                Description = adapter.Description,
                                IpAddress = ip,
                                InterfaceType = typeStr,
                                IsPrimary = false
                            });
                        }
                    }
                }
            }

            // Marcar la primaria (dar prioridad a Wi-Fi primero para comanderas móviles, luego Ethernet)
            var primaryCandidate = results.FirstOrDefault(r => r.InterfaceType == "Wi-Fi") 
                                   ?? results.FirstOrDefault(r => r.InterfaceType == "Ethernet") 
                                   ?? results.FirstOrDefault();

            if (primaryCandidate != null)
            {
                primaryCandidate.IsPrimary = true;
            }
        }
        catch (Exception ex)
        {
            // 8B-B4: catch vacío convertido en aviso (se devuelve lo recopilado hasta el fallo).
            Core.Logging.AppLogger.LogWarn($"[NETWORK_DISCOVERY] Error recopilando interfaces de red: {ex.Message}");
        }

        return results;
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        // 10.0.0.0/8
        if (bytes[0] == 10) return true;

        // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;

        return false;
    }
}
