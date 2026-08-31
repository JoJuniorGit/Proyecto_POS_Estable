using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

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
    public List<NetworkInterfaceInfo> NetworkInterfaces { get; set; } = new();
    public string QrPayload { get; set; } = string.Empty;
}

public interface INetworkDiscoveryService
{
    ServerPairingInfo GetPairingInfo(int httpPort = 5000, int httpsPort = 5001);
    List<NetworkInterfaceInfo> GetPhysicalIPv4Interfaces();
}

public class NetworkDiscoveryService : INetworkDiscoveryService
{
    private static readonly string[] VirtualKeywords = new[]
    {
        "virtual", "hyper-v", "wsl", "docker", "vmware", "vethernet",
        "default switch", "bluetooth", "npcap", "tailscale", "zerotier",
        "wireguard", "vpn", "loopback", "pseudo", "teredo", "isatap"
    };

    public ServerPairingInfo GetPairingInfo(int httpPort = 5000, int httpsPort = 5001)
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
        var httpsUrl = $"https://{primaryIp}:{httpsPort}";

        return new ServerPairingInfo
        {
            ServerName = machineName,
            MachineName = machineName,
            PrimaryIpAddress = primaryIp,
            HttpPort = httpPort,
            HttpsPort = httpsPort,
            PrimaryHttpUrl = httpUrl,
            PrimaryHttpsUrl = httpsUrl,
            NetworkInterfaces = interfaces,
            QrPayload = $"{httpUrl}/?paired=true"
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
        catch
        {
            // Silenciosamente retornar lo que se haya podido recopilar
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
