using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ConnectionSettingsNavigationTests
{
    private readonly Mock<IPaymentService> _mockPaymentService = new();
    private readonly Mock<ISettingsService> _mockSettingsService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<IConnectionManager> _mockConnectionManager = new();
    private readonly UserSession _userSession = new();

    public ConnectionSettingsNavigationTests()
    {
        _mockPaymentService.Setup(p => p.GetAllMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto>());
        _mockSettingsService.Setup(s => s.GetTimeZoneAsync())
            .ReturnsAsync("America/Caracas");
        _mockConnectionManager.SetupGet(c => c.CurrentServerAddress)
            .Returns("http://localhost:5000/");
        _mockConnectionManager.SetupGet(c => c.Status)
            .Returns(ConnectionStatus.Connected);
    }

    [Fact]
    public async Task SettingsViewModel_OpenServerConnectionCommand_InvokesDialogService_And_Refreshes_Address()
    {
        // Arrange
        var initialServer = "http://localhost:5000/";
        var newServer = "http://192.168.1.100:5000/";
        var currentAddr = initialServer;

        _mockConnectionManager.SetupGet(c => c.CurrentServerAddress)
            .Returns(() => currentAddr);

        _mockDialogService.Setup(d => d.ShowServerConnectionDialogAsync())
            .Callback(() => { currentAddr = newServer; })
            .ReturnsAsync(true);

        using var vm = new SettingsViewModel(
            _mockPaymentService.Object,
            _mockSettingsService.Object,
            _userSession,
            _mockDialogService.Object,
            _mockConnectionManager.Object);

        Assert.Equal(initialServer, vm.CurrentServerAddress);

        // Act
        await vm.OpenServerConnectionCommand.ExecuteAsync(null);

        // Assert
        _mockDialogService.Verify(d => d.ShowServerConnectionDialogAsync(), Times.Once);
        Assert.Equal(newServer, vm.CurrentServerAddress);
        Assert.Equal("Conectado", vm.ConnectionStatusText);
    }

    [Fact]
    public async Task SettingsViewModel_OpenPairingQrCommand_InvokesDialogService()
    {
        // Arrange
        _mockDialogService.Setup(d => d.ShowPairingQrDialogAsync())
            .Returns(Task.CompletedTask);

        using var vm = new SettingsViewModel(
            _mockPaymentService.Object,
            _mockSettingsService.Object,
            _userSession,
            _mockDialogService.Object,
            _mockConnectionManager.Object);

        // Act
        await vm.OpenPairingQrCommand.ExecuteAsync(null);

        // Assert
        _mockDialogService.Verify(d => d.ShowPairingQrDialogAsync(), Times.Once);
    }

    [Fact]
    public void SettingsViewModel_ListensTo_ConnectionStatusChanged_And_Updates_Properties()
    {
        // Arrange
        using var vm = new SettingsViewModel(
            _mockPaymentService.Object,
            _mockSettingsService.Object,
            _userSession,
            _mockDialogService.Object,
            _mockConnectionManager.Object);

        Assert.Equal("http://localhost:5000/", vm.CurrentServerAddress);
        Assert.Equal("Conectado", vm.ConnectionStatusText);
        Assert.Equal("#27AE60", vm.ConnectionStatusColor);

        // Act - Simulate Disconnection Event
        _mockConnectionManager.Raise(m => m.ConnectionStatusChanged += null,
            new ConnectionStatusEventArgs
            {
                ServerAddress = "http://192.168.1.50:5000/",
                Status = ConnectionStatus.Disconnected
            });

        // Assert
        Assert.Equal("http://192.168.1.50:5000/", vm.CurrentServerAddress);
        Assert.Equal("Desconectado", vm.ConnectionStatusText);
        Assert.Equal("#E74C3C", vm.ConnectionStatusColor);

        // Act - Simulate Reconnecting / Scanning Event
        _mockConnectionManager.Raise(m => m.ConnectionStatusChanged += null,
            new ConnectionStatusEventArgs
            {
                ServerAddress = "http://192.168.1.50:5000/",
                Status = ConnectionStatus.Connecting
            });

        Assert.Equal("Conectando...", vm.ConnectionStatusText);
        Assert.Equal("#F39C12", vm.ConnectionStatusColor);
    }

    [Fact]
    public void SettingsViewModel_Dispose_UnsubscribesFrom_ConnectionManager_Event()
    {
        // Arrange
        var vm = new SettingsViewModel(
            _mockPaymentService.Object,
            _mockSettingsService.Object,
            _userSession,
            _mockDialogService.Object,
            _mockConnectionManager.Object);

        Assert.Equal("http://localhost:5000/", vm.CurrentServerAddress);

        // Act - Dispose the ViewModel
        vm.Dispose();

        // Simulate Event after Dispose
        _mockConnectionManager.Raise(m => m.ConnectionStatusChanged += null,
            new ConnectionStatusEventArgs
            {
                ServerAddress = "http://10.0.0.99:5000/",
                Status = ConnectionStatus.Disconnected
            });

        // Assert - Address must remain the old one, not updated
        Assert.Equal("http://localhost:5000/", vm.CurrentServerAddress);
    }

    [Fact]
    public async Task MainViewModel_OpenPairingQrCommand_InvokesDialogService()
    {
        // Arrange
        _mockDialogService.Setup(d => d.ShowPairingQrDialogAsync())
            .Returns(Task.CompletedTask);

        var mainVm = CreateMainViewModel();

        // Act
        await mainVm.OpenPairingQrCommand.ExecuteAsync(null);

        // Assert
        _mockDialogService.Verify(d => d.ShowPairingQrDialogAsync(), Times.Once);
    }

    [Fact]
    public async Task MainViewModel_OpenServerConnectionCommand_InvokesDialogService()
    {
        // Arrange
        _mockDialogService.Setup(d => d.ShowServerConnectionDialogAsync())
            .ReturnsAsync(true);

        var mainVm = CreateMainViewModel();

        // Act
        await mainVm.OpenServerConnectionCommand.ExecuteAsync(null);

        // Assert
        _mockDialogService.Verify(d => d.ShowServerConnectionDialogAsync(), Times.Once);
    }

    private MainViewModel CreateMainViewModel()
    {
        var loginVm = (LoginViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LoginViewModel));
        var posVm = (PosViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PosViewModel));
        var invVm = (InventoryViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InventoryViewModel));
        var historyVm = (SalesHistoryViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SalesHistoryViewModel));
        var pendingOrdersVm = (PendingOrdersViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PendingOrdersViewModel));
        var pendingPickupsVm = (PendingPickupsViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PendingPickupsViewModel));
        var settingsVm = new SettingsViewModel(_mockPaymentService.Object, _mockSettingsService.Object, _userSession, _mockDialogService.Object, _mockConnectionManager.Object);
        var exchangeVm = (ExchangeRateViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ExchangeRateViewModel));
        var cashVm = (CashDrawerViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CashDrawerViewModel));
        var importVm = (ImportProductsViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ImportProductsViewModel));
        var dailyVm = (DailyClosureViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DailyClosureViewModel));
        var usersVm = (UsersManagementViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UsersManagementViewModel));
        var mockHealth = new Mock<IHealthPollingService>();

        return new MainViewModel(
            _userSession,
            loginVm,
            posVm,
            invVm,
            historyVm,
            pendingOrdersVm,
            pendingPickupsVm,
            settingsVm,
            exchangeVm,
            cashVm,
            importVm,
            dailyVm,
            usersVm,
            mockHealth.Object,
            _mockDialogService.Object);
    }
}
