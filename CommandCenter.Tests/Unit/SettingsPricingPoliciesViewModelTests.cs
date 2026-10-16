using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.184-T4 (S2): "Políticas de Precios y Redondeo" en Ajustes. La carga es silenciosa (ante error
/// rigen los defaults ExactMath / ExactMath / piso activo y no se persiste nada); cada cambio de
/// regla o del toggle auto-guarda los TRES campos juntos y, si el PUT falla, la UI revierte al
/// último estado aplicado mostrando el error.
/// </summary>
public class SettingsPricingPoliciesViewModelTests
{
    private readonly Mock<IPaymentService> _paymentServiceMock = new();
    private readonly Mock<ISettingsService> _settingsServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();

    private PricingPoliciesDto? _lastSavedPolicies;

    private SettingsViewModel CreateViewModel()
    {
        _paymentServiceMock
            .Setup(service => service.GetAllMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto>());

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Admin", Role = UserRole.Admin, Cedula = "V-1", IsActive = true });

        return new SettingsViewModel(
            _paymentServiceMock.Object,
            _settingsServiceMock.Object,
            session,
            _dialogServiceMock.Object);
    }

    private void SetupPolicies(PricingPoliciesDto policies)
        => _settingsServiceMock
            .Setup(service => service.GetPricingPoliciesAsync())
            .ReturnsAsync(policies);

    private void CaptureSaves()
        => _settingsServiceMock
            .Setup(service => service.SetPricingPoliciesAsync(It.IsAny<PricingPoliciesDto>()))
            .Callback<PricingPoliciesDto>(policies => _lastSavedPolicies = policies)
            .Returns(Task.CompletedTask);

    [Fact]
    public async Task EnsureLoadedAsync_LoadsConfiguredPoliciesWithoutPersisting()
    {
        SetupPolicies(new PricingPoliciesDto
        {
            UsdRoundingRule = RoundingRuleType.UsdPsychological,
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5,
            EnforceMinimumMarginFloor = false
        });
        using var vm = CreateViewModel();

        await vm.EnsureLoadedAsync();

        Assert.Equal(RoundingRuleType.UsdPsychological, vm.SelectedUsdRoundingRule?.Rule);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, vm.SelectedCashBsRoundingRule?.Rule);
        Assert.False(vm.EnforceMinimumMarginFloor);
        _settingsServiceMock.Verify(
            service => service.SetPricingPoliciesAsync(It.IsAny<PricingPoliciesDto>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureLoadedAsync_WhenServerFails_FallsBackToDefaultsSilently()
    {
        _settingsServiceMock
            .Setup(service => service.GetPricingPoliciesAsync())
            .ThrowsAsync(new InvalidOperationException("Connection refused"));
        using var vm = CreateViewModel();

        await vm.EnsureLoadedAsync();

        Assert.Equal(RoundingRuleType.ExactMath, vm.SelectedUsdRoundingRule?.Rule);
        Assert.Equal(RoundingRuleType.ExactMath, vm.SelectedCashBsRoundingRule?.Rule);
        Assert.True(vm.EnforceMinimumMarginFloor);
        _dialogServiceMock.Verify(
            d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        _settingsServiceMock.Verify(
            service => service.SetPricingPoliciesAsync(It.IsAny<PricingPoliciesDto>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureLoadedAsync_WithUnknownStoredRule_FallsBackToExactMathOption()
    {
        SetupPolicies(new PricingPoliciesDto
        {
            UsdRoundingRule = (RoundingRuleType)99,
            CashBsRoundingRule = (RoundingRuleType)98
        });
        using var vm = CreateViewModel();

        await vm.EnsureLoadedAsync();

        Assert.Equal(RoundingRuleType.ExactMath, vm.SelectedUsdRoundingRule?.Rule);
        Assert.Equal(RoundingRuleType.ExactMath, vm.SelectedCashBsRoundingRule?.Rule);
    }

    [Fact]
    public async Task ChangingUsdRule_AutoSavesTheThreeFieldsTogether()
    {
        CaptureSaves();
        using var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        vm.SelectedUsdRoundingRule = vm.UsdRoundingOptions.Single(option => option.Rule == RoundingRuleType.UsdPsychological);
        await vm.PricingPoliciesSaveTask;

        Assert.NotNull(_lastSavedPolicies);
        Assert.Equal(RoundingRuleType.UsdPsychological, _lastSavedPolicies!.UsdRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, _lastSavedPolicies.CashBsRoundingRule);
        Assert.True(_lastSavedPolicies.EnforceMinimumMarginFloor);
    }

    [Fact]
    public async Task ChangingCashRule_AutoSavesTheThreeFieldsTogether()
    {
        CaptureSaves();
        using var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        vm.SelectedCashBsRoundingRule = vm.CashBsRoundingOptions.Single(option => option.Rule == RoundingRuleType.VesCashFriendlyStep10);
        await vm.PricingPoliciesSaveTask;

        Assert.NotNull(_lastSavedPolicies);
        Assert.Equal(RoundingRuleType.ExactMath, _lastSavedPolicies!.UsdRoundingRule);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, _lastSavedPolicies.CashBsRoundingRule);
        Assert.True(_lastSavedPolicies.EnforceMinimumMarginFloor);
    }

    [Fact]
    public async Task TogglingMarginFloor_AutoSavesTheThreeFieldsTogether()
    {
        CaptureSaves();
        using var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        vm.EnforceMinimumMarginFloor = false;
        await vm.PricingPoliciesSaveTask;

        Assert.NotNull(_lastSavedPolicies);
        Assert.False(_lastSavedPolicies!.EnforceMinimumMarginFloor);
        Assert.Equal(RoundingRuleType.ExactMath, _lastSavedPolicies.UsdRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, _lastSavedPolicies.CashBsRoundingRule);
    }

    [Fact]
    public async Task WhenSaveFails_ToggleRevertsToLastAppliedStateAndShowsError()
    {
        using var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();
        _settingsServiceMock
            .Setup(service => service.SetPricingPoliciesAsync(It.IsAny<PricingPoliciesDto>()))
            .ThrowsAsync(new InvalidOperationException("Server down"));

        vm.EnforceMinimumMarginFloor = false;
        await vm.PricingPoliciesSaveTask;

        Assert.True(vm.EnforceMinimumMarginFloor);
        _dialogServiceMock.Verify(
            d => d.ShowError("Políticas de Precios y Redondeo", It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenSaveFails_ComboRevertsToLastAppliedOptionAndShowsError()
    {
        using var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();
        _settingsServiceMock
            .Setup(service => service.SetPricingPoliciesAsync(It.IsAny<PricingPoliciesDto>()))
            .ThrowsAsync(new InvalidOperationException("Server down"));

        vm.SelectedUsdRoundingRule = vm.UsdRoundingOptions.Single(option => option.Rule == RoundingRuleType.UsdPsychological);
        await vm.PricingPoliciesSaveTask;

        Assert.Equal(RoundingRuleType.ExactMath, vm.SelectedUsdRoundingRule?.Rule);
        _dialogServiceMock.Verify(
            d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }
}
