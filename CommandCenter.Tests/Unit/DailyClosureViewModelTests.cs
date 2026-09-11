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

public class DailyClosureViewModelTests
{
    private readonly Mock<IDailyClosureClientService> _closureServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly Mock<IFilePickerDialog> _filePickerMock = new();

    private DailyClosureViewModel CreateViewModel(UserSession? session = null)
    {
        return new DailyClosureViewModel(
            _closureServiceMock.Object,
            _dialogServiceMock.Object,
            session,
            _filePickerMock.Object);
    }

    [Fact]
    public async Task ConfirmClosureCommand_WhenDetailRowsEmpty_ShowsWarningDialog()
    {
        var vm = CreateViewModel();

        await vm.ConfirmClosureCommand.ExecuteAsync(null);

        _dialogServiceMock.Verify(d => d.ShowWarning("Advertencia", It.IsAny<string>()), Times.Once);
        _closureServiceMock.Verify(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmClosureCommand_WhenUserCancels_DoesNotProceedWithCreation()
    {
        _closureServiceMock
            .Setup(s => s.GetExpectedTotalsAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ExpectedAmountBsS = 100m }
            });

        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        var vm = CreateViewModel();
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);

        await vm.ConfirmClosureCommand.ExecuteAsync(null);

        _dialogServiceMock.Verify(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _closureServiceMock.Verify(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmClosureCommand_WhenUserConfirms_CallsCreateClosureAndShowsSuccess()
    {
        _closureServiceMock
            .Setup(s => s.GetExpectedTotalsAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ExpectedAmountBsS = 100m },
                new() { PaymentMethodId = 2, PaymentMethodName = "Punto de Venta", ExpectedAmountBsS = 250m }
            });

        _closureServiceMock
            .Setup(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()))
            .ReturnsAsync(new DailyClosureDto { Id = 5, TotalExpectedBsS = 350m, TotalActualBsS = 350m });

        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var userSession = new UserSession();
        userSession.CurrentUser = new UserDto { Id = 1, Name = "Admin User", Role = UserRole.Admin };

        var vm = CreateViewModel(userSession);
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);

        vm.DetailRows[0].ActualAmountBsS = 100m;
        vm.DetailRows[1].ActualAmountBsS = 250m;

        await vm.ConfirmClosureCommand.ExecuteAsync(null);

        _closureServiceMock.Verify(s => s.CreateClosureAsync(It.Is<CreateClosureRequest>(r =>
            r.Details.Count == 2 &&
            r.Details[0].ActualAmountBsS == 100m &&
            r.Details[1].ActualAmountBsS == 250m)), Times.Once);

        _dialogServiceMock.Verify(d => d.ShowInfo("Éxito de Cierre", It.IsAny<string>()), Times.Once);
        _closureServiceMock.Verify(s => s.GetExpectedTotalsAsync(It.IsAny<DateTime>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LoadExpectedTotalsCommand_WhenInvoked_CalculatesExpectedAndDifferenceTotals()
    {
        _closureServiceMock
            .Setup(s => s.GetExpectedTotalsAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ExpectedAmountBsS = 500m },
                new() { PaymentMethodId = 2, PaymentMethodName = "Pago Móvil", ExpectedAmountBsS = 300m }
            });

        var vm = CreateViewModel();
        await vm.LoadExpectedTotalsCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.DetailRows.Count);
        Assert.Equal(800m, vm.TotalExpectedBsS);
        Assert.Equal(0m, vm.TotalActualBsS);
        Assert.Equal(-800m, vm.TotalDifferenceBsS);

        vm.DetailRows[0].ActualAmountBsS = 500m;
        vm.DetailRows[1].ActualAmountBsS = 350m;

        Assert.Equal(850m, vm.TotalActualBsS);
        Assert.Equal(50m, vm.TotalDifferenceBsS);
        Assert.Contains("SOBRANTE EN CAJA", vm.DifferenceStatusLabel);
    }

    [Fact]
    public void BlindClosing_WhenUserIsCashier_DisallowsDisabling()
    {
        var cashierSession = new UserSession();
        cashierSession.CurrentUser = new UserDto { Id = 2, Name = "Cajero 1", Role = UserRole.Cashier };

        var vm = CreateViewModel(cashierSession);

        Assert.True(vm.IsBlindClosing);
        Assert.False(vm.CanToggleBlindClosing);

        vm.IsBlindClosing = false;
        Assert.True(vm.IsBlindClosing);
    }

    [Fact]
    public void ExportClosureReceiptCommand_WhenDetailRowsPresent_InvokesFilePicker()
    {
        _closureServiceMock
            .Setup(s => s.GetExpectedTotalsAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ExpectedAmountBsS = 100m }
            });

        _filePickerMock
            .Setup(f => f.PickSaveFilePath(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(string.Empty);

        var vm = CreateViewModel();
        vm.DetailRows.Add(new ClosureDetailRow(1, "Efectivo", 100m, () => { }));

        vm.ExportClosureReceiptCommand.Execute(null);

        _filePickerMock.Verify(f => f.PickSaveFilePath(
            "Guardar Comprobante de Cierre",
            "Archivo de Texto (*.txt)|*.txt",
            It.IsAny<string>()), Times.Once);
    }
}
