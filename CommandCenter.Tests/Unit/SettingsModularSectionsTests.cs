using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// S1/S2/S9: the settings shell exposes five independent sections. Switching sections is a pure
/// presentation concern: the ViewModel retains the selected index, keeps the in-memory state and
/// never re-runs the idempotent load pipeline or any network/service call.
/// </summary>
public class SettingsModularSectionsTests
{
    private readonly Mock<IPaymentService> _paymentService = new();
    private readonly Mock<ISettingsService> _settingsService = new();

    private SettingsViewModel CreateViewModel()
    {
        _paymentService
            .Setup(service => service.GetAllMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto>
            {
                new() { Id = 1, Name = "Efectivo Bs.S", IsCash = true, IsActive = true, Currency = "Bs.S", DisplayOrder = 0 },
                new() { Id = 2, Name = "Zelle", IsCash = false, IsActive = true, Currency = "USD", DisplayOrder = 1 }
            });
        _settingsService
            .Setup(service => service.GetCurrencyFormatAsync())
            .ReturnsAsync("Venezuelan");
        _settingsService
            .Setup(service => service.GetTimeZoneAsync())
            .ReturnsAsync(string.Empty);
        _settingsService
            .Setup(service => service.GetAllowNegativeStockAsync())
            .ReturnsAsync(false);
        _settingsService
            .Setup(service => service.GetPricingPoliciesAsync())
            .ReturnsAsync(new PricingPoliciesDto());
        _settingsService
            .Setup(service => service.GetCashAdvanceCommissionAsync())
            .ReturnsAsync(new CashAdvanceCommissionSettingsDto
            {
                TransferCommissionPct = 7m,
                CashCommissionPct = 5m
            });

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Admin", Role = UserRole.Admin, Cedula = "V-1", IsActive = true });

        return new SettingsViewModel(_paymentService.Object, _settingsService.Object, session);
    }

    [Fact]
    public void SelectedSettingsSectionIndex_DefaultsToFirstSection()
    {
        using var vm = CreateViewModel();

        Assert.Equal(0, vm.SelectedSettingsSectionIndex);
    }

    [Fact]
    public void SettingSection_DoesNotTriggerAnyLoad()
    {
        using var vm = CreateViewModel();

        vm.SelectedSettingsSectionIndex = 2;

        Assert.Equal(2, vm.SelectedSettingsSectionIndex);
        _paymentService.Verify(service => service.GetAllMethodsAsync(), Times.Never);
        _settingsService.Verify(service => service.GetCurrencyFormatAsync(), Times.Never);
        _settingsService.Verify(service => service.GetTimeZoneAsync(), Times.Never);
        _settingsService.Verify(service => service.GetAllowNegativeStockAsync(), Times.Never);
        _settingsService.Verify(service => service.GetPricingPoliciesAsync(), Times.Never);
        _settingsService.Verify(service => service.GetCashAdvanceCommissionAsync(), Times.Never);
    }

    [Fact]
    public async Task SwitchingSections_KeepsSelectionAndAvoidsReloads()
    {
        using var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Equal(2, vm.PaymentMethods.Count);
        VerifyLoads(Times.Once());

        foreach (var section in new[] { 1, 2, 3, 4 })
        {
            vm.SelectedSettingsSectionIndex = section;
            await vm.EnsureLoadedAsync();

            Assert.Equal(section, vm.SelectedSettingsSectionIndex);
            Assert.Equal(2, vm.PaymentMethods.Count);
        }

        // EnsureLoadedAsync stays idempotent: navigating sections never re-fetches or persists.
        VerifyLoads(Times.Once());
        _settingsService.Verify(service => service.SetAllowNegativeStockAsync(It.IsAny<bool>()), Times.Never);
        _settingsService.Verify(service => service.SetPricingPoliciesAsync(It.IsAny<PricingPoliciesDto>()), Times.Never);
        _settingsService.Verify(
            service => service.SetCashAdvanceCommissionAsync(It.IsAny<CashAdvanceCommissionSettingsDto>()),
            Times.Never);
    }

    private void VerifyLoads(Times times)
    {
        _paymentService.Verify(service => service.GetAllMethodsAsync(), times);
        _settingsService.Verify(service => service.GetCurrencyFormatAsync(), times);
        _settingsService.Verify(service => service.GetTimeZoneAsync(), times);
        _settingsService.Verify(service => service.GetAllowNegativeStockAsync(), times);
        _settingsService.Verify(service => service.GetPricingPoliciesAsync(), times);
        _settingsService.Verify(service => service.GetCashAdvanceCommissionAsync(), times);
    }
}
