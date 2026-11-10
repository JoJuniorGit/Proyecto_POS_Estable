using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// F4-WU1 (Paso 14): fee-rule management inside "Métodos de Pago y Caja". The backend endpoint is
/// Admin-only, so Managers never load or mutate rules; invalid percentages/effective windows are
/// rejected locally before touching the API.
/// </summary>
public sealed class SettingsPaymentMethodFeesViewModelTests
{
    private readonly Mock<IPaymentService> _paymentService = new();
    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly Mock<IDialogService> _dialogService = new();
    private readonly Mock<IProfitabilityClientService> _profitabilityService = new();

    private SettingsViewModel CreateViewModel(UserRole role = UserRole.Admin)
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Usuario Test", Role = role, IsActive = true });
        return new SettingsViewModel(
            _paymentService.Object,
            _settingsService.Object,
            session,
            _dialogService.Object,
            profitabilityService: _profitabilityService.Object);
    }

    [Fact]
    public async Task Load_AsAdmin_MapsRulesAndEnablesEditing()
    {
        _profitabilityService
            .Setup(service => service.GetFeeRulesAsync(
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PaymentMethodFeeRuleClientDto> { BuildRule() });
        using var vm = CreateViewModel(UserRole.Admin);
        vm.PaymentMethods.Add(new PaymentMethodDto { Id = 2, Name = "Punto de Venta", IsCash = false });

        await vm.LoadPaymentMethodFeeRulesAsync();

        Assert.True(vm.CanEditPaymentMethodFeeRules);
        Assert.Single(vm.PaymentMethodFeeRules);
        Assert.Single(vm.FeeEligiblePaymentMethods);
        Assert.Equal("Punto de Venta", vm.PaymentMethodFeeRules[0].PaymentMethodName);
        Assert.False(vm.HasFeeRuleStatus);
    }

    [Fact]
    public async Task Load_AsManager_HidesCardAndNeverCallsApi()
    {
        using var vm = CreateViewModel(UserRole.Manager);

        await vm.LoadPaymentMethodFeeRulesAsync();

        Assert.False(vm.CanEditPaymentMethodFeeRules);
        Assert.Empty(vm.PaymentMethodFeeRules);
        _profitabilityService.Verify(service => service.GetFeeRulesAsync(
            It.IsAny<int?>(),
            It.IsAny<bool?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_AsAdmin_CreatesRuleWithParsedValues()
    {
        _profitabilityService
            .Setup(service => service.GetFeeRulesAsync(
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PaymentMethodFeeRuleClientDto>());
        CreatePaymentMethodFeeRuleClientRequest? captured = null;
        _profitabilityService
            .Setup(service => service.CreateFeeRuleAsync(
                It.IsAny<CreatePaymentMethodFeeRuleClientRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<CreatePaymentMethodFeeRuleClientRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(BuildRule());
        using var vm = CreateViewModel(UserRole.Admin);
        vm.PaymentMethods.Add(new PaymentMethodDto { Id = 2, Name = "Punto de Venta", IsCash = false });
        await vm.LoadPaymentMethodFeeRulesAsync();

        vm.NewFeeRuleCommand.Execute(null);
        vm.SelectedFeePaymentMethod = vm.FeeEligiblePaymentMethods.Single();
        vm.SelectedFeeTypeOption = vm.FeeTypeOptions.Single(option => option.Value == PaymentMethodFeeTypeClient.Percentage);
        vm.FeePercentageText = "1,5";
        vm.FeeCurrencyCode = "USD";
        vm.FeeEffectiveFromDate = new DateTime(2026, 11, 1);
        vm.FeeEffectiveToDate = null;
        vm.IsFeeRuleActive = true;

        await vm.SaveFeeRuleCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.PaymentMethodId);
        Assert.Equal(PaymentMethodFeeTypeClient.Percentage, captured.FeeType);
        Assert.Equal(1.5m, captured.Percentage);
        Assert.Null(captured.FixedAmount);
        Assert.Equal("USD", captured.CurrencyCode);
        Assert.True(captured.IsActive);
        Assert.Single(vm.PaymentMethodFeeRules);
        Assert.Contains("guardada", vm.FeeRuleStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_AsAdmin_UpdatesExistingRule()
    {
        var rule = BuildRule();
        _profitabilityService
            .Setup(service => service.GetFeeRulesAsync(
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PaymentMethodFeeRuleClientDto> { rule });
        UpdatePaymentMethodFeeRuleClientRequest? captured = null;
        _profitabilityService
            .Setup(service => service.UpdateFeeRuleAsync(
                rule.Id,
                It.IsAny<UpdatePaymentMethodFeeRuleClientRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, UpdatePaymentMethodFeeRuleClientRequest, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(BuildRule(percentage: 2.25m));
        using var vm = CreateViewModel(UserRole.Admin);
        vm.PaymentMethods.Add(new PaymentMethodDto { Id = 2, Name = "Punto de Venta", IsCash = false });
        await vm.LoadPaymentMethodFeeRulesAsync();

        vm.EditFeeRuleCommand.Execute(rule);
        Assert.Equal(rule.Id, vm.EditingFeeRuleId);
        vm.FeePercentageText = "2,25";

        await vm.SaveFeeRuleCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(PaymentMethodFeeTypeClient.Percentage, captured!.FeeType);
        Assert.Equal(2.25m, captured.Percentage);
        _profitabilityService.Verify(service => service.CreateFeeRuleAsync(
            It.IsAny<CreatePaymentMethodFeeRuleClientRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_AsAdmin_ConfirmsAndSoftDeletes()
    {
        var rule = BuildRule();
        _profitabilityService
            .Setup(service => service.GetFeeRulesAsync(
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PaymentMethodFeeRuleClientDto> { rule });
        _dialogService
            .Setup(dialog => dialog.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        _profitabilityService
            .Setup(service => service.DeleteFeeRuleAsync(rule.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        using var vm = CreateViewModel(UserRole.Admin);
        await vm.LoadPaymentMethodFeeRulesAsync();

        await vm.DeleteFeeRuleCommand.ExecuteAsync(rule);

        _profitabilityService.Verify(service => service.DeleteFeeRuleAsync(
            rule.Id,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(rule.IsActive);
        Assert.Contains("desactivada", vm.FeeRuleStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_WithPercentageAboveHundred_DoesNotCallApi()
    {
        _profitabilityService
            .Setup(service => service.GetFeeRulesAsync(
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PaymentMethodFeeRuleClientDto>());
        using var vm = CreateViewModel(UserRole.Admin);
        vm.PaymentMethods.Add(new PaymentMethodDto { Id = 2, Name = "Punto de Venta", IsCash = false });
        await vm.LoadPaymentMethodFeeRulesAsync();

        vm.NewFeeRuleCommand.Execute(null);
        vm.SelectedFeePaymentMethod = vm.FeeEligiblePaymentMethods.Single();
        vm.SelectedFeeTypeOption = vm.FeeTypeOptions.Single(option => option.Value == PaymentMethodFeeTypeClient.Percentage);
        vm.FeePercentageText = "120";
        vm.FeeCurrencyCode = "USD";
        vm.FeeEffectiveFromDate = new DateTime(2026, 11, 1);

        await vm.SaveFeeRuleCommand.ExecuteAsync(null);

        Assert.Contains("porcentaje", vm.FeeRuleStatus, StringComparison.OrdinalIgnoreCase);
        _profitabilityService.Verify(service => service.CreateFeeRuleAsync(
            It.IsAny<CreatePaymentMethodFeeRuleClientRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_WithInvalidEffectiveRange_DoesNotCallApi()
    {
        _profitabilityService
            .Setup(service => service.GetFeeRulesAsync(
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PaymentMethodFeeRuleClientDto>());
        using var vm = CreateViewModel(UserRole.Admin);
        vm.PaymentMethods.Add(new PaymentMethodDto { Id = 2, Name = "Punto de Venta", IsCash = false });
        await vm.LoadPaymentMethodFeeRulesAsync();

        vm.NewFeeRuleCommand.Execute(null);
        vm.SelectedFeePaymentMethod = vm.FeeEligiblePaymentMethods.Single();
        vm.SelectedFeeTypeOption = vm.FeeTypeOptions.Single(option => option.Value == PaymentMethodFeeTypeClient.Fixed);
        vm.FeeFixedAmountText = "0,25";
        vm.FeeCurrencyCode = "USD";
        vm.FeeEffectiveFromDate = new DateTime(2026, 11, 10);
        vm.FeeEffectiveToDate = new DateTime(2026, 11, 1);

        await vm.SaveFeeRuleCommand.ExecuteAsync(null);

        Assert.Contains("vigencia", vm.FeeRuleStatus, StringComparison.OrdinalIgnoreCase);
        _profitabilityService.Verify(service => service.CreateFeeRuleAsync(
            It.IsAny<CreatePaymentMethodFeeRuleClientRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_AsManager_IsBlocked()
    {
        using var vm = CreateViewModel(UserRole.Manager);

        await vm.SaveFeeRuleCommand.ExecuteAsync(null);

        Assert.False(vm.CanEditPaymentMethodFeeRules);
        _profitabilityService.Verify(service => service.CreateFeeRuleAsync(
            It.IsAny<CreatePaymentMethodFeeRuleClientRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static PaymentMethodFeeRuleClientDto BuildRule(decimal percentage = 1.5m) => new()
    {
        Id = 7,
        PaymentMethodId = 2,
        PaymentMethodName = "Punto de Venta",
        FeeType = PaymentMethodFeeTypeClient.Percentage,
        Percentage = percentage,
        CurrencyCode = "USD",
        EffectiveFromUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        IsActive = true
    };
}
