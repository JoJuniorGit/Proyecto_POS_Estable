using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// WPF Settings permissions and validation for the cash advance commission card. Admin can save;
/// Manager can read but never writes; out-of-range values never reach the API.
/// </summary>
public sealed class SettingsCashAdvanceCommissionViewModelTests
{
    private readonly Mock<IPaymentService> _paymentService = new();
    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly Mock<IDialogService> _dialogService = new();

    private SettingsViewModel CreateViewModel(UserRole role)
    {
        var session = new UserSession
        {
            CurrentUser = new UserDto { Id = 1, Name = "Usuario Test", Role = role }
        };

        return new SettingsViewModel(
            _paymentService.Object,
            _settingsService.Object,
            session,
            _dialogService.Object);
    }

    [Fact]
    public async Task Load_MapsBothChannelsAndEnablesAdminEditing()
    {
        _settingsService
            .Setup(service => service.GetCashAdvanceCommissionAsync())
            .ReturnsAsync(new CashAdvanceCommissionSettingsDto
            {
                TransferCommissionPct = 7m,
                CashCommissionPct = 5m
            });
        using var vm = CreateViewModel(UserRole.Admin);

        await vm.LoadCashAdvanceCommissionAsync();

        Assert.Equal("7", vm.TransferCommissionText);
        Assert.Equal("5", vm.CashCommissionText);
        Assert.True(vm.CanEditCashAdvanceCommission);
        Assert.False(vm.HasCashAdvanceCommissionStatus);
    }

    [Fact]
    public async Task Load_DecimalCommission_UsesTheActiveNumericFormat()
    {
        var previous = CurrencyDisplay.Current;
        try
        {
            _settingsService
                .Setup(service => service.GetCashAdvanceCommissionAsync())
                .ReturnsAsync(new CashAdvanceCommissionSettingsDto
                {
                    TransferCommissionPct = 7.5m,
                    CashCommissionPct = 5.25m
                });
            using var vm = CreateViewModel(UserRole.Admin);

            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            await vm.LoadCashAdvanceCommissionAsync();

            Assert.Equal("7,5", vm.TransferCommissionText);
            Assert.Equal("5,25", vm.CashCommissionText);

            CurrencyDisplay.Current = MoneyDisplayFormat.International;
            await vm.LoadCashAdvanceCommissionAsync();

            Assert.Equal("7.5", vm.TransferCommissionText);
            Assert.Equal("5.25", vm.CashCommissionText);
        }
        finally
        {
            CurrencyDisplay.Current = previous;
        }
    }

    [Fact]
    public async Task Load_UnconfiguredChannels_ShowsWarning()
    {
        _settingsService
            .Setup(service => service.GetCashAdvanceCommissionAsync())
            .ReturnsAsync(new CashAdvanceCommissionSettingsDto());
        using var vm = CreateViewModel(UserRole.Admin);

        await vm.LoadCashAdvanceCommissionAsync();

        Assert.True(vm.HasCashAdvanceCommissionStatus);
        Assert.Contains("no configuradas", vm.CashAdvanceCommissionStatus, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_AsAdmin_SendsBothPercentages()
    {
        _settingsService
            .Setup(service => service.SetCashAdvanceCommissionAsync(It.IsAny<CashAdvanceCommissionSettingsDto>()))
            .Returns(Task.CompletedTask);
        using var vm = CreateViewModel(UserRole.Admin);
        vm.TransferCommissionText = "7";
        vm.CashCommissionText = "5";

        await vm.SaveCashAdvanceCommissionCommand.ExecuteAsync(null);

        _settingsService.Verify(
            service => service.SetCashAdvanceCommissionAsync(
                It.Is<CashAdvanceCommissionSettingsDto>(settings =>
                    settings.TransferCommissionPct == 7m &&
                    settings.CashCommissionPct == 5m)),
            Times.Once);
        Assert.Equal("Comisiones guardadas correctamente.", vm.CashAdvanceCommissionStatus);
    }

    [Fact]
    public async Task Save_AsManager_IsBlocked()
    {
        using var vm = CreateViewModel(UserRole.Manager);
        vm.TransferCommissionText = "7";
        vm.CashCommissionText = "5";

        await vm.SaveCashAdvanceCommissionCommand.ExecuteAsync(null);

        Assert.False(vm.CanEditCashAdvanceCommission);
        _settingsService.Verify(
            service => service.SetCashAdvanceCommissionAsync(It.IsAny<CashAdvanceCommissionSettingsDto>()),
            Times.Never);
    }

    [Fact]
    public async Task Save_OutOfRange_DoesNotCallTheApi()
    {
        using var vm = CreateViewModel(UserRole.Admin);
        vm.TransferCommissionText = "0";
        vm.CashCommissionText = "5";

        await vm.SaveCashAdvanceCommissionCommand.ExecuteAsync(null);

        Assert.Contains("transferencia", vm.CashAdvanceCommissionStatus, System.StringComparison.OrdinalIgnoreCase);
        _settingsService.Verify(
            service => service.SetCashAdvanceCommissionAsync(It.IsAny<CashAdvanceCommissionSettingsDto>()),
            Times.Never);
    }
}
