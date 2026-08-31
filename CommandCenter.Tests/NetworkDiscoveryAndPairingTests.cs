using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Backend.API.Services;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests;

public class NetworkDiscoveryAndPairingTests
{
    [Fact]
    public void NetworkDiscoveryService_ReturnsValidPairingInfo()
    {
        // Arrange
        var service = new NetworkDiscoveryService();

        // Act
        var info = service.GetPairingInfo(httpPort: 5000, httpsPort: 5001);

        // Assert
        Assert.NotNull(info);
        Assert.False(string.IsNullOrWhiteSpace(info.ServerName));
        Assert.False(string.IsNullOrWhiteSpace(info.PrimaryIpAddress));
        Assert.Equal(5000, info.HttpPort);
        Assert.Equal(5001, info.HttpsPort);
        Assert.StartsWith("http://", info.PrimaryHttpUrl);
        Assert.Contains(":5000", info.PrimaryHttpUrl);
        Assert.Contains("?paired=true", info.QrPayload);
    }

    [Fact]
    public void NetworkDiscoveryService_FiltersVirtualInterfaces()
    {
        // Arrange
        var service = new NetworkDiscoveryService();

        // Act
        var interfaces = service.GetPhysicalIPv4Interfaces();

        // Assert
        Assert.NotNull(interfaces);
        foreach (var iface in interfaces)
        {
            Assert.False(iface.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase));
            Assert.False(iface.IpAddress.StartsWith("127."));
            Assert.False(iface.IpAddress.StartsWith("169.254."));
        }
    }

    [Fact]
    public void ClientSettingsStore_SavesAndLoadsCorrectly()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), $"pos_test_settings_{Guid.NewGuid():N}.json");
        try
        {
            var store = new ClientSettingsStore(tempFile);

            // Act
            store.UpdateServerAddress("http://192.168.1.99:5000/", "TEST-SERVER");
            var loaded = store.LoadSettings();

            // Assert
            Assert.Equal("http://192.168.1.99:5000/", loaded.ServerBaseAddress);
            Assert.Equal("TEST-SERVER", loaded.LastKnownServerMachineName);
            Assert.Equal("192.168.1.99", loaded.LastKnownServerIp);
            Assert.NotNull(loaded.LastUpdatedUtc);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SubnetScanner_ProbeSingleHost_ReturnsNullForInvalidHost()
    {
        // Arrange
        var scanner = new SubnetScannerService();

        // Act
        var result = await scanner.ProbeSingleHostAsync("999.999.999.999", 5000, timeoutMs: 100);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ConnectionManager_InitializesWithStoredAddress()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), $"pos_test_conn_{Guid.NewGuid():N}.json");
        try
        {
            var store = new ClientSettingsStore(tempFile);
            store.UpdateServerAddress("http://192.168.1.200:5000/", "TEST-BOX");
            var scanner = new SubnetScannerService();
            var manager = new ConnectionManager(store, scanner);

            // Assert
            Assert.Equal("http://192.168.1.200:5000/", manager.CurrentServerAddress);
            Assert.Equal("TEST-BOX", manager.CurrentMachineName);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
