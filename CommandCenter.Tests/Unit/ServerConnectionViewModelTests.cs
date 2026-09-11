using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ServerConnectionViewModelTests
{
    private readonly Mock<IConnectionManager> _mockConnectionManager = new();
    private readonly Mock<ISubnetScannerService> _mockScannerService = new();

    public ServerConnectionViewModelTests()
    {
        _mockConnectionManager.SetupGet(c => c.CurrentServerAddress)
            .Returns("http://localhost:5000/");
    }

    [Fact]
    public void Constructor_WhenConnectionManagerNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ServerConnectionViewModel(null!, _mockScannerService.Object));
    }

    [Fact]
    public void Constructor_WhenScannerServiceNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ServerConnectionViewModel(_mockConnectionManager.Object, null!));
    }

    [Fact]
    public void Constructor_WhenCurrentAddressIsLocalhost_InitializesLocalModeTrue()
    {
        _mockConnectionManager.SetupGet(c => c.CurrentServerAddress)
            .Returns("http://localhost:5000/");

        using var vm = new ServerConnectionViewModel(_mockConnectionManager.Object, _mockScannerService.Object);

        Assert.True(vm.IsLocalServerMode);
        Assert.False(vm.IsRemoteServerMode);
        Assert.Equal("http://localhost:5000/", vm.ServerAddress);
    }

    [Fact]
    public void Constructor_WhenCurrentAddressIsRemote_InitializesRemoteModeTrue()
    {
        _mockConnectionManager.SetupGet(c => c.CurrentServerAddress)
            .Returns("http://192.168.1.150:5000/");

        using var vm = new ServerConnectionViewModel(_mockConnectionManager.Object, _mockScannerService.Object);

        Assert.False(vm.IsLocalServerMode);
        Assert.True(vm.IsRemoteServerMode);
        Assert.Equal("http://192.168.1.150:5000/", vm.ServerAddress);
    }

    [Fact]
    public void OnIsLocalServerModeChanged_WhenSetToTrue_SetsLocalhostAddress()
    {
        _mockConnectionManager.SetupGet(c => c.CurrentServerAddress)
            .Returns("http://192.168.1.150:5000/");

        using var vm = new ServerConnectionViewModel(_mockConnectionManager.Object, _mockScannerService.Object);
        vm.IsLocalServerMode = true;

        Assert.Equal("http://localhost:5000/", vm.ServerAddress);
        Assert.False(vm.IsRemoteServerMode);
    }

    [Fact]
    public async Task ScanNetworkAsync_WhenSingleServerFound_SetsSingularStatusMessage()
    {
        var singleServer = new DiscoveredServer
        {
            IpAddress = "192.168.1.50",
            MachineName = "CAJA-01",
            BaseUrl = "http://192.168.1.50:5000/",
            ResponseTimeMs = 8,
            IsHealthy = true
        };

        _mockScannerService.Setup(s => s.ScanSubnetAsync(
                It.IsAny<IProgress<int>>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscoveredServer> { singleServer });

        using var vm = new ServerConnectionViewModel(_mockConnectionManager.Object, _mockScannerService.Object);

        await vm.ScanNetworkCommand.ExecuteAsync(null);

        Assert.Single(vm.DiscoveredServers);
        Assert.Equal("Se encontró 1 servidor POS en la red.", vm.StatusMessage);
        Assert.Equal(singleServer, vm.SelectedServer);
        Assert.Equal(singleServer.BaseUrl, vm.ServerAddress);
    }

    [Fact]
    public async Task ScanNetworkAsync_WhenMultipleServersFound_SetsPluralStatusMessage()
    {
        var server1 = new DiscoveredServer
        {
            IpAddress = "192.168.1.50",
            MachineName = "CAJA-01",
            BaseUrl = "http://192.168.1.50:5000/",
            ResponseTimeMs = 5,
            IsHealthy = true
        };
        var server2 = new DiscoveredServer
        {
            IpAddress = "192.168.1.60",
            MachineName = "CAJA-02",
            BaseUrl = "http://192.168.1.60:5000/",
            ResponseTimeMs = 12,
            IsHealthy = true
        };

        _mockScannerService.Setup(s => s.ScanSubnetAsync(
                It.IsAny<IProgress<int>>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscoveredServer> { server1, server2 });

        using var vm = new ServerConnectionViewModel(_mockConnectionManager.Object, _mockScannerService.Object);

        await vm.ScanNetworkCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.DiscoveredServers.Count);
        Assert.Equal("Se encontraron 2 servidores POS en la red.", vm.StatusMessage);
        Assert.Equal(server1, vm.SelectedServer);
    }

    [Fact]
    public async Task ScanNetworkAsync_WhenNoServersFound_SetsNoServersDetectedStatusMessage()
    {
        _mockScannerService.Setup(s => s.ScanSubnetAsync(
                It.IsAny<IProgress<int>>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscoveredServer>());

        using var vm = new ServerConnectionViewModel(_mockConnectionManager.Object, _mockScannerService.Object);

        await vm.ScanNetworkCommand.ExecuteAsync(null);

        Assert.Empty(vm.DiscoveredServers);
        Assert.Equal("No se detectaron servidores POS activos en la subred local.", vm.StatusMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenProbeSuccessful_SetsSuccessAndStatus()
    {
        var probeResult = new DiscoveredServer
        {
            IpAddress = "192.168.1.100",
            MachineName = "CAJA-SERVER",
            BaseUrl = "http://192.168.1.100:5000/",
            ResponseTimeMs = 6,
            IsHealthy = true
        };

        _mockScannerService.Setup(s => s.ProbeSingleHostAsync(
                "http://192.168.1.100:5000/", 5000, 1500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(probeResult);

        using var vm = new ServerConnectionViewModel(_mockConnectionManager.Object, _mockScannerService.Object);
        vm.ServerAddress = "http://192.168.1.100:5000/";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.TestSuccess);
        Assert.Contains("Conexión exitosa", vm.TestResultMessage);
        Assert.Contains("Conexión exitosa", vm.StatusMessage);
    }

    [Fact]
    public void OnServerAddressChanged_WhenAddressChanged_ResetsTestResult()
    {
        using var vm = new ServerConnectionViewModel(_mockConnectionManager.Object, _mockScannerService.Object);
        vm.TestResultMessage = "Conexión exitosa";
        vm.TestSuccess = true;

        vm.ServerAddress = "http://192.168.1.200:5000/";

        Assert.Empty(vm.TestResultMessage);
        Assert.Null(vm.TestSuccess);
    }

    [Fact]
    public async Task ScanNetworkAsync_WhenServersFound_SetsHasDiscoveredServersTrue()
    {
        var server = new DiscoveredServer
        {
            IpAddress = "192.168.1.50",
            MachineName = "CAJA-01",
            BaseUrl = "http://192.168.1.50:5000/",
            ResponseTimeMs = 8,
            IsHealthy = true
        };

        _mockScannerService.Setup(s => s.ScanSubnetAsync(
                It.IsAny<IProgress<int>>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscoveredServer> { server });

        using var vm = new ServerConnectionViewModel(_mockConnectionManager.Object, _mockScannerService.Object);
        Assert.False(vm.HasDiscoveredServers);

        await vm.ScanNetworkCommand.ExecuteAsync(null);

        Assert.True(vm.HasDiscoveredServers);
    }

    [Fact]
    public void Dispose_WhenCalledMultipleTimes_ExecutesWithoutException()
    {
        var vm = new ServerConnectionViewModel(_mockConnectionManager.Object, _mockScannerService.Object);

        vm.Dispose();
        var ex = Record.Exception(() => vm.Dispose());
        Assert.Null(ex);
    }
}
