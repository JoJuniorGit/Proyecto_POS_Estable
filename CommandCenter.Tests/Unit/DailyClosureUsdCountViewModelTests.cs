using System;
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
/// Residual 8.187-8.188: el cierre diario WPF acepta un conteo USD opcional y muestra el arqueo
/// USD (esperado/diferencia) solo cuando el rol supervisor y el modo no ciego lo permiten.
/// </summary>
public class DailyClosureUsdCountViewModelTests
{
    private readonly Mock<IDailyClosureClientService> _closureService = new();
    private readonly Mock<IDialogService> _dialogService = new();
    private readonly Mock<IFilePickerDialog> _filePicker = new();

    private DailyClosureViewModel CreateViewModel(UserRole? role = null)
    {
        var session = new UserSession();
        if (role.HasValue)
        {
            session.CurrentUser = new UserDto { Id = 1, Name = "Usuario", Role = role.Value };
        }

        _closureService
            .Setup(s => s.GetExpectedTotalsAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ExpectedAmountBsS = 500m }
            });
        _dialogService
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        return new DailyClosureViewModel(
            _closureService.Object,
            _dialogService.Object,
            session,
            _filePicker.Object);
    }

    private static DailyClosureDto CreateUsdResult() => new()
    {
        Id = 5,
        TotalExpectedBsS = 500m,
        TotalActualBsS = 500m,
        ExpectedClosingBalanceUsd = 12.5m,
        ActualClosingBalanceUsd = 13.25m,
        DifferenceUsd = 0.75m
    };

    [Fact]
    public async Task ConfirmClosure_WithUsdCount_SendsOptionalFieldAndCapturesReconciliation()
    {
        _closureService
            .Setup(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()))
            .ReturnsAsync(CreateUsdResult());

        var vm = CreateViewModel(UserRole.Admin);
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);
        vm.DetailRows[0].ActualAmountBsS = 500m;
        vm.ActualClosingBalanceUsdText = "13,25";

        await vm.ConfirmClosureCommand.ExecuteAsync(null);

        _closureService.Verify(s => s.CreateClosureAsync(It.Is<CreateClosureRequest>(request =>
            request.ActualClosingBalanceUsd == 13.25m)), Times.Once);
        Assert.Equal(13.25m, vm.LastCreatedClosure?.ActualClosingBalanceUsd);
        Assert.Equal(12.5m, vm.LastCreatedClosure?.ExpectedClosingBalanceUsd);
        Assert.Equal(0.75m, vm.LastCreatedClosure?.DifferenceUsd);
    }

    [Fact]
    public async Task ConfirmClosure_WithoutUsdCount_SendsNullAndKeepsLegacyBehavior()
    {
        _closureService
            .Setup(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()))
            .ReturnsAsync(new DailyClosureDto { Id = 6 });

        var vm = CreateViewModel(UserRole.Admin);
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);
        vm.DetailRows[0].ActualAmountBsS = 500m;

        await vm.ConfirmClosureCommand.ExecuteAsync(null);

        _closureService.Verify(s => s.CreateClosureAsync(It.Is<CreateClosureRequest>(request =>
            request.ActualClosingBalanceUsd == null)), Times.Once);
    }

    [Fact]
    public async Task ConfirmClosure_WithInvalidUsdCount_ShowsWarningAndDoesNotSubmit()
    {
        var vm = CreateViewModel(UserRole.Admin);
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);
        vm.DetailRows[0].ActualAmountBsS = 500m;
        vm.ActualClosingBalanceUsdText = "abc";

        await vm.ConfirmClosureCommand.ExecuteAsync(null);

        _dialogService.Verify(d => d.ShowWarning("Advertencia", It.Is<string>(m => m.Contains("USD"))), Times.Once);
        _closureService.Verify(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmClosure_WithNegativeUsdCount_ShowsWarningAndDoesNotSubmit()
    {
        var vm = CreateViewModel(UserRole.Admin);
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);
        vm.DetailRows[0].ActualAmountBsS = 500m;
        vm.ActualClosingBalanceUsdText = "-5";

        await vm.ConfirmClosureCommand.ExecuteAsync(null);

        _dialogService.Verify(d => d.ShowWarning("Advertencia", It.Is<string>(m => m.Contains("negativo"))), Times.Once);
        _closureService.Verify(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()), Times.Never);
    }

    [Fact]
    public async Task UsdReconciliation_Admin_ShowsExpectedAndDifference()
    {
        _closureService
            .Setup(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()))
            .ReturnsAsync(CreateUsdResult());

        var vm = CreateViewModel(UserRole.Admin);
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);
        vm.DetailRows[0].ActualAmountBsS = 500m;
        vm.ActualClosingBalanceUsdText = "13.25";

        await vm.ConfirmClosureCommand.ExecuteAsync(null);

        Assert.True(vm.CanViewUsdExpected);
        Assert.True(vm.ShowUsdReconciliation);
        Assert.Equal("$ 12.5000", vm.ExpectedClosingBalanceUsdDisplay);
        Assert.Equal("+$ 0.7500", vm.DifferenceUsdDisplay);
    }

    [Fact]
    public async Task UsdReconciliation_Manager_ShowsExpectedAndDifference()
    {
        _closureService
            .Setup(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()))
            .ReturnsAsync(CreateUsdResult());

        var vm = CreateViewModel(UserRole.Manager);
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);
        vm.DetailRows[0].ActualAmountBsS = 500m;
        vm.ActualClosingBalanceUsdText = "13.25";

        await vm.ConfirmClosureCommand.ExecuteAsync(null);

        Assert.True(vm.CanViewUsdExpected);
        Assert.True(vm.ShowUsdReconciliation);
    }

    [Fact]
    public async Task UsdReconciliation_Cashier_StaysBlindEvenIfServerReturnsValues()
    {
        _closureService
            .Setup(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()))
            .ReturnsAsync(CreateUsdResult());

        var vm = CreateViewModel(UserRole.Cashier);
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);
        vm.DetailRows[0].ActualAmountBsS = 500m;
        vm.ActualClosingBalanceUsdText = "13.25";

        await vm.ConfirmClosureCommand.ExecuteAsync(null);

        Assert.True(vm.IsBlindClosing);
        Assert.False(vm.CanViewUsdExpected);
        Assert.False(vm.ShowUsdReconciliation);
        Assert.Equal("—", vm.ExpectedClosingBalanceUsdDisplay);
        Assert.Equal("—", vm.DifferenceUsdDisplay);
    }

    [Fact]
    public async Task UsdReconciliation_AdminCanHideItWithBlindToggle()
    {
        _closureService
            .Setup(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()))
            .ReturnsAsync(CreateUsdResult());

        var vm = CreateViewModel(UserRole.Admin);
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);
        vm.DetailRows[0].ActualAmountBsS = 500m;
        vm.ActualClosingBalanceUsdText = "13.25";

        await vm.ConfirmClosureCommand.ExecuteAsync(null);
        vm.IsBlindClosing = true;

        Assert.False(vm.CanViewUsdExpected);
        Assert.False(vm.ShowUsdReconciliation);
        Assert.Equal("—", vm.ExpectedClosingBalanceUsdDisplay);
    }
}
