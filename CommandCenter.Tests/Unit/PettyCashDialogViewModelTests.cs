using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4 (Paso 13 / 8.188, S1/S4/S5): modales de caja chica. El registro muestra la equivalencia
/// dual a la tasa vigente (el servidor fija el snapshot), valida los datos obligatorios y, ante
/// un 403, abre el flujo de autorización con el contexto exacto y reintenta con el token.
/// </summary>
public class PettyCashDialogViewModelTests
{
    private static PettyCashExpenseClientDto NewExpense(
        PettyCashCurrencyClient currency = PettyCashCurrencyClient.Usd,
        PettyCashStatusClient status = PettyCashStatusClient.Active) => new()
    {
        Id = 7,
        VoucherNumber = 1,
        SessionId = 3,
        Currency = currency,
        Category = PettyCashCategoryClient.MantenimientoReparaciones,
        AmountBsS = 1275.00m,
        AmountUsd = 25.5000m,
        HistoricalRate = 50.0000m,
        Concept = "Reparación de vitrina",
        Beneficiary = "Ferretería Central",
        ReferenceNumber = "FAC-0091",
        CreatedByUserId = 4,
        AuthorizedByUserId = 9,
        Status = status
    };

    private static PettyCashExpenseDialogViewModel NewDialog(
        Mock<ICashDrawerService> cash,
        Mock<IDialogService> dialog,
        decimal rate = 50m)
    {
        var vm = new PettyCashExpenseDialogViewModel(cash.Object, dialog.Object, sessionId: 3, exchangeRate: rate);
        vm.SelectedCurrency = vm.CurrencyOptions.Single(option => option.Value == PettyCashCurrencyClient.Usd);
        vm.AmountText = "25.5";
        vm.SelectedCategory = vm.CategoryOptions.Single(option => option.Value == PettyCashCategoryClient.MantenimientoReparaciones);
        vm.Concept = "Reparación de vitrina";
        vm.Beneficiary = "Ferretería Central";
        vm.ReferenceNumber = "FAC-0091";
        return vm;
    }

    [Fact]
    public void Dialog_ComputesLiveDualEquivalent_ForBothCurrencies()
    {
        var vm = NewDialog(new Mock<ICashDrawerService>(), new Mock<IDialogService>(), rate: 40m);

        vm.SelectedCurrency = vm.CurrencyOptions.Single(option => option.Value == PettyCashCurrencyClient.BsS);
        vm.AmountText = "1000";
        Assert.False(vm.IsUsd);
        Assert.Equal(25m, vm.EquivalentAmount);
        Assert.Equal("Monto (Bs.S)", vm.AmountHint);

        vm.SelectedCurrency = vm.CurrencyOptions.Single(option => option.Value == PettyCashCurrencyClient.Usd);
        Assert.True(vm.IsUsd);
        Assert.Equal(40000m, vm.EquivalentAmount);
        Assert.Equal("Monto (USD)", vm.AmountHint);
    }

    [Fact]
    public void Dialog_ValidatesRequiredFieldsAndRate()
    {
        var withoutRate = NewDialog(new Mock<ICashDrawerService>(), new Mock<IDialogService>(), rate: 0m);
        Assert.Contains("tasa", withoutRate.ValidationMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.False(withoutRate.CanConfirm);

        var vm = NewDialog(new Mock<ICashDrawerService>(), new Mock<IDialogService>(), rate: 50m);

        vm.AmountText = "0";
        Assert.Contains("mayor a cero", vm.ValidationMessage!, StringComparison.OrdinalIgnoreCase);

        vm.AmountText = "25.5";
        vm.Concept = " ";
        Assert.Contains("concepto", vm.ValidationMessage!, StringComparison.OrdinalIgnoreCase);

        vm.Concept = "Reparación";
        vm.Beneficiary = string.Empty;
        Assert.Contains("beneficiario", vm.ValidationMessage!, StringComparison.OrdinalIgnoreCase);

        vm.Beneficiary = "Ferretería";
        vm.ReferenceNumber = string.Empty;
        Assert.Contains("referencia", vm.ValidationMessage!, StringComparison.OrdinalIgnoreCase);

        vm.ReferenceNumber = "FAC-1";
        vm.SelectedCategory = null;
        Assert.Contains("categoría", vm.ValidationMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dialog_ConfirmWithoutAuthorizationToken_RegistersAndClosesWithVoucher()
    {
        var expected = NewExpense();
        var cash = new Mock<ICashDrawerService>();
        cash.Setup(s => s.RegisterPettyCashExpenseAsync(
                It.IsAny<PettyCashExpenseClientRequest>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var dialog = new Mock<IDialogService>();
        var vm = NewDialog(cash, dialog);
        bool? closed = null;
        vm.RequestClose += success => closed = success;

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Same(expected, vm.Result);
        cash.Verify(s => s.RegisterPettyCashExpenseAsync(
            It.Is<PettyCashExpenseClientRequest>(request =>
                request.Currency == PettyCashCurrencyClient.Usd
                && request.Amount == 25.5m
                && request.Category == PettyCashCategoryClient.MantenimientoReparaciones
                && request.Concept == "Reparación de vitrina"
                && request.Beneficiary == "Ferretería Central"
                && request.ReferenceNumber == "FAC-0091"
                && !string.IsNullOrWhiteSpace(request.IdempotencyKey)),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
        dialog.Verify(d => d.ShowAuthorizationWaitAsync(
            It.IsAny<IAuthorizationRequestContext>(),
            It.IsAny<Func<string, Task>>()), Times.Never);
    }

    [Fact]
    public async Task Dialog_Confirm403_OpensAuthorizationWithExactContext_AndRetriesWithToken()
    {
        var expected = NewExpense();
        var context = new PettyCashExpenseAuthorizationRequestContext
        {
            SessionId = 3,
            Currency = PettyCashCurrencyClient.Usd,
            AmountBsS = 1275.00m,
            AmountUsd = 25.5000m,
            Category = PettyCashCategoryClient.MantenimientoReparaciones
        };

        var cash = new Mock<ICashDrawerService>();
        cash.Setup(s => s.RegisterPettyCashExpenseAsync(
                It.IsAny<PettyCashExpenseClientRequest>(),
                null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PettyCashAuthorizationException("Se requiere autorización.", context));
        cash.Setup(s => s.RegisterPettyCashExpenseAsync(
                It.IsAny<PettyCashExpenseClientRequest>(),
                "tok-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        PettyCashExpenseAuthorizationRequestContext? captured = null;
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(
                It.IsAny<IAuthorizationRequestContext>(),
                It.IsAny<Func<string, Task>>()))
            .Returns(async (IAuthorizationRequestContext requestContext, Func<string, Task> retry) =>
            {
                captured = Assert.IsType<PettyCashExpenseAuthorizationRequestContext>(requestContext);
                await retry("tok-1");
                return new AuthorizationWaitResult(AuthorizationWaitOutcome.Granted);
            });

        var vm = NewDialog(cash, dialog);
        bool? closed = null;
        vm.RequestClose += success => closed = success;

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Same(expected, vm.Result);
        Assert.NotNull(captured);
        Assert.Equal(3, captured!.SessionId);
        Assert.Equal(PettyCashCurrencyClient.Usd, captured.Currency);
        Assert.Equal(1275.00m, captured.AmountBsS);
        Assert.Equal(25.5000m, captured.AmountUsd);
        Assert.Equal(PettyCashCategoryClient.MantenimientoReparaciones, captured.Category);
        cash.Verify(s => s.RegisterPettyCashExpenseAsync(
            It.IsAny<PettyCashExpenseClientRequest>(),
            "tok-1",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dialog_AuthorizationRejected_KeepsDialogOpenWithMessage()
    {
        var context = new PettyCashExpenseAuthorizationRequestContext
        {
            SessionId = 3,
            Currency = PettyCashCurrencyClient.BsS,
            AmountBsS = 1000m,
            AmountUsd = 20m,
            Category = PettyCashCategoryClient.Varios
        };

        var cash = new Mock<ICashDrawerService>();
        cash.Setup(s => s.RegisterPettyCashExpenseAsync(
                It.IsAny<PettyCashExpenseClientRequest>(),
                null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PettyCashAuthorizationException("Se requiere autorización.", context));

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(
                It.IsAny<IAuthorizationRequestContext>(),
                It.IsAny<Func<string, Task>>()))
            .ReturnsAsync(new AuthorizationWaitResult(AuthorizationWaitOutcome.Rejected, "Sin cupo", "No aprobado"));

        var vm = NewDialog(cash, dialog);
        bool closed = false;
        vm.RequestClose += success => closed = success;

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Null(vm.Result);
        Assert.Contains("No aprobado", vm.ErrorMessage);
    }

    [Fact]
    public async Task CancelDialog_RequiresReasonAndCancelsWithExactSnapshots()
    {
        var expense = NewExpense();
        var cancelled = NewExpense(status: PettyCashStatusClient.Cancelled);
        var cash = new Mock<ICashDrawerService>();
        cash.Setup(s => s.CancelPettyCashExpenseAsync(
                7,
                "Gasto duplicado",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cancelled);

        var dialog = new Mock<IDialogService>();
        var vm = new PettyCashCancelDialogViewModel(cash.Object, dialog.Object, expense);

        vm.Reason = "  ";
        await vm.ConfirmCommand.ExecuteAsync(null);
        cash.Verify(s => s.CancelPettyCashExpenseAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("motivo", vm.ValidationMessage!, StringComparison.OrdinalIgnoreCase);

        bool? closed = null;
        vm.RequestClose += success => closed = success;
        vm.Reason = "Gasto duplicado";
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Same(cancelled, vm.Result);
    }

    [Fact]
    public async Task CancelDialog_403OpensAuthorizationAndRetriesWithToken()
    {
        var expense = NewExpense();
        var cancelled = NewExpense(status: PettyCashStatusClient.Cancelled);
        var context = new PettyCashExpenseAuthorizationRequestContext
        {
            SessionId = expense.SessionId,
            Currency = expense.Currency,
            AmountBsS = expense.AmountBsS,
            AmountUsd = expense.AmountUsd,
            Category = expense.Category
        };

        var cash = new Mock<ICashDrawerService>();
        cash.Setup(s => s.CancelPettyCashExpenseAsync(
                7,
                "Gasto duplicado",
                null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PettyCashAuthorizationException("Se requiere autorización.", context));
        cash.Setup(s => s.CancelPettyCashExpenseAsync(
                7,
                "Gasto duplicado",
                "tok-9",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cancelled);

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(
                It.IsAny<IAuthorizationRequestContext>(),
                It.IsAny<Func<string, Task>>()))
            .Returns(async (IAuthorizationRequestContext _, Func<string, Task> retry) =>
            {
                await retry("tok-9");
                return new AuthorizationWaitResult(AuthorizationWaitOutcome.Granted);
            });

        var vm = new PettyCashCancelDialogViewModel(cash.Object, dialog.Object, expense) { Reason = "Gasto duplicado" };
        bool? closed = null;
        vm.RequestClose += success => closed = success;

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Same(cancelled, vm.Result);
        cash.Verify(s => s.CancelPettyCashExpenseAsync(
            7,
            "Gasto duplicado",
            "tok-9",
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
