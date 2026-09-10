using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SystemRestartTests
{
    private static UserSession CreateAdminSession()
    {
        var session = new UserSession();
        session.SetUser(new UserDto
        {
            Id = 1,
            Name = "Admin",
            Role = UserRole.Admin,
            IsActive = true
        });
        return session;
    }

    [Fact]
    public async Task SettingsViewModel_RestartSystemAsync_WhenAdminConfirms_InvokesRestartServiceAndShowsInfo()
    {
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(s => s.GetAllowNegativeStockAsync()).ReturnsAsync(false);
        mockSettings.Setup(s => s.RestartSystemAsync()).Returns(Task.CompletedTask);
        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        using var vm = new SettingsViewModel(
            new Mock<IPaymentService>().Object,
            mockSettings.Object,
            CreateAdminSession(),
            mockDialog.Object);

        await vm.RestartSystemCommand.ExecuteAsync(null);

        mockSettings.Verify(s => s.RestartSystemAsync(), Times.Once);
        mockDialog.Verify(d => d.ShowConfirm("Reiniciar Sistema", It.IsAny<string>()), Times.Once);
        mockDialog.Verify(d => d.ShowInfo("Reinicio en curso", It.IsAny<string>()), Times.Once);
        Assert.False(vm.IsRestarting);
    }

    [Fact]
    public async Task SettingsViewModel_RestartSystemAsync_WhenNotConfirmed_DoesNotCallRestartService()
    {
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(s => s.GetAllowNegativeStockAsync()).ReturnsAsync(false);
        mockSettings.Setup(s => s.RestartSystemAsync()).Returns(Task.CompletedTask);
        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        using var vm = new SettingsViewModel(
            new Mock<IPaymentService>().Object,
            mockSettings.Object,
            CreateAdminSession(),
            mockDialog.Object);

        await vm.RestartSystemCommand.ExecuteAsync(null);

        mockSettings.Verify(s => s.RestartSystemAsync(), Times.Never);
        mockDialog.Verify(d => d.ShowInfo("Reinicio en curso", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SettingsViewModel_RestartSystemAsync_WhenServiceFails_ShowsError()
    {
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(s => s.GetAllowNegativeStockAsync()).ReturnsAsync(false);
        mockSettings.Setup(s => s.RestartSystemAsync())
            .ThrowsAsync(new System.Net.Http.HttpRequestException("sin conexión"));
        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        using var vm = new SettingsViewModel(
            new Mock<IPaymentService>().Object,
            mockSettings.Object,
            CreateAdminSession(),
            mockDialog.Object);

        await vm.RestartSystemCommand.ExecuteAsync(null);

        mockDialog.Verify(d => d.ShowError("Reinicio de Sistema", It.IsAny<string>()), Times.Once);
        Assert.False(vm.IsRestarting);
    }
}