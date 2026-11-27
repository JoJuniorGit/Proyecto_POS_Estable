using System;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class LoginViewModelLifecycleTests
{
    private static Mock<IConnectionManager> CreateConnectionManager()
    {
        var connection = new Mock<IConnectionManager>();
        connection.SetupGet(c => c.CurrentServerAddress).Returns("https://localhost:5001/");
        connection.SetupGet(c => c.Status).Returns(ConnectionStatus.Connected);
        return connection;
    }

    private static LoginViewModel CreateViewModel(IConnectionManager connection)
        => new(new Mock<IUserService>().Object, new Mock<IDialogService>().Object, new UserSession(), connection);

    [Fact]
    public void LoginViewModel_KeepsConnectionIndicatorLive_AcrossRepeatedViewLifecycles()
    {
        var connection = CreateConnectionManager();
        var vm = CreateViewModel(connection.Object);

        Assert.True(vm.IsServerConnected);

        for (int cycle = 0; cycle < 3; cycle++)
        {
            connection.SetupGet(c => c.Status).Returns(ConnectionStatus.Scanning);
            connection.Raise(m => m.ConnectionStatusChanged += null,
                new ConnectionStatusEventArgs { Status = ConnectionStatus.Scanning, ServerAddress = "https://localhost:5001/" });
            Assert.True(vm.IsScanningServer);

            connection.SetupGet(c => c.Status).Returns(ConnectionStatus.Connected);
            connection.SetupGet(c => c.CurrentServerAddress).Returns("https://192.168.1.10:5001/");
            connection.Raise(m => m.ConnectionStatusChanged += null,
                new ConnectionStatusEventArgs { Status = ConnectionStatus.Connected, ServerAddress = "https://192.168.1.10:5001/" });
            Assert.True(vm.IsServerConnected);
            Assert.Equal("192.168.1.10:5001", vm.ServerAddressText);
        }
    }

    [Fact]
    public void LoginViewModel_Dispose_UnsubscribesFromConnectionManager_AndIsIdempotent()
    {
        var connection = CreateConnectionManager();
        var vm = CreateViewModel(connection.Object);

        Assert.True(vm.IsServerConnected);

        vm.Dispose();
        vm.Dispose();

        connection.SetupGet(c => c.Status).Returns(ConnectionStatus.Disconnected);
        connection.Raise(m => m.ConnectionStatusChanged += null,
            new ConnectionStatusEventArgs { Status = ConnectionStatus.Disconnected, ServerAddress = "https://10.0.0.99:5001/" });

        Assert.True(vm.IsServerConnected);
        Assert.Equal("localhost:5001", vm.ServerAddressText);
    }
}
