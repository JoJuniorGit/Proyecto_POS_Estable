using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Desktop.Client.Converters;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T4c2b4 (S9): the daily closure view model and its presentation converters resolve local
/// and primary amounts, labels and dual-only visibility from the active monetary profile. Single
/// collapses the local display to the primary currency and hides the USD reconciliation duplicate.
/// Raw decimals, closure payloads, parsing, commands and authorization rules stay unchanged.
/// </summary>
public class DailyClosureMonetaryProfileDisplayTests
{
    private static void ApplyDualProfile() => CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "VES",
        SecondaryCurrencySymbol = "Bs.S",
        IsConfigured = true
    });

    private static void ApplySingleEurProfile() => CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
    {
        Mode = MonetaryMode.Single,
        PrimaryCurrencyCode = "EUR",
        PrimaryCurrencySymbol = "€",
        SecondaryCurrencyCode = null,
        SecondaryCurrencySymbol = null,
        IsConfigured = true
    });

    private static DailyClosureViewModel CreateViewModel(
        Mock<IDailyClosureClientService> closureService,
        Mock<IDialogService> dialogService)
    {
        var session = new UserSession();
        session.CurrentUser = new UserDto { Id = 1, Name = "Supervisor", Role = UserRole.Admin };

        return new DailyClosureViewModel(
            closureService.Object,
            dialogService.Object,
            session,
            Mock.Of<IFilePickerDialog>());
    }

    private static void SetupTotals(Mock<IDailyClosureClientService> closureService, decimal expected = 500m)
    {
        closureService
            .Setup(s => s.GetExpectedTotalsAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ExpectedAmountBsS = expected }
            });
    }

    private static DailyClosureDto CreateUsdResult() => new()
    {
        Id = 5,
        TotalExpectedBsS = 500m,
        TotalActualBsS = 550m,
        TotalDifferenceBsS = 50m,
        ExpectedClosingBalanceUsd = 12.5m,
        ActualClosingBalanceUsd = 13.25m,
        DifferenceUsd = 0.75m
    };

    [Fact]
    public async Task DualProfile_UsesSecondarySymbolAndKeepsRawDecimals()
    {
        var previousFormat = CurrencyDisplay.Current;
        var closureService = new Mock<IDailyClosureClientService>();
        var dialogService = new Mock<IDialogService>();
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplyDualProfile();
            SetupTotals(closureService);

            var viewModel = CreateViewModel(closureService, dialogService);
            try
            {
                await viewModel.LoadExpectedTotalsCommand.ExecuteAsync(null);
                viewModel.DetailRows[0].ActualAmountBsS = 550m;

                Assert.Equal("SOBRANTE EN CAJA (+50,00 Bs.S)", viewModel.DifferenceStatusLabel);

                // Raw decimals remain the calculation source; only their presentation changed.
                Assert.Equal(500m, viewModel.TotalExpectedBsS);
                Assert.Equal(550m, viewModel.TotalActualBsS);
                Assert.Equal(50m, viewModel.TotalDifferenceBsS);
                Assert.Equal(500m, viewModel.DetailRows[0].ExpectedAmountBsS);
                Assert.Equal(550m, viewModel.DetailRows[0].ActualAmountBsS);
                Assert.Equal(50m, viewModel.DetailRows[0].DifferenceBsS);

                viewModel.TotalDifferenceBsS = -50m;
                Assert.Equal("FALTANTE EN CAJA (-50,00 Bs.S)", viewModel.DifferenceStatusLabel);

                viewModel.TotalDifferenceBsS = 0m;
                Assert.Equal("CUADRADO EXACTO", viewModel.DifferenceStatusLabel);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public async Task DualProfile_UsdReconciliationUsesPrimarySymbolAndKeepsTheClosurePayload()
    {
        var previousFormat = CurrencyDisplay.Current;
        var closureService = new Mock<IDailyClosureClientService>();
        var dialogService = new Mock<IDialogService>();
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplyDualProfile();
            SetupTotals(closureService);
            closureService
                .Setup(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()))
                .ReturnsAsync(CreateUsdResult());
            dialogService
                .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(true);

            var viewModel = CreateViewModel(closureService, dialogService);
            try
            {
                await viewModel.LoadExpectedTotalsCommand.ExecuteAsync(null);
                viewModel.DetailRows[0].ActualAmountBsS = 550m;
                viewModel.ActualClosingBalanceUsdText = "13,25";

                await viewModel.ConfirmClosureCommand.ExecuteAsync(null);

                Assert.Equal("$ 12,50", viewModel.ExpectedClosingBalanceUsdDisplay);
                Assert.Equal("+$ 0,75", viewModel.DifferenceUsdDisplay);

                Assert.Equal(12.5m, viewModel.LastCreatedClosure?.ExpectedClosingBalanceUsd);
                Assert.Equal(13.25m, viewModel.LastCreatedClosure?.ActualClosingBalanceUsd);
                Assert.Equal(0.75m, viewModel.LastCreatedClosure?.DifferenceUsd);

                closureService.Verify(s => s.CreateClosureAsync(It.Is<CreateClosureRequest>(request =>
                    request.Details.Count == 1 &&
                    request.Details[0].ExpectedAmountBsS == 500m &&
                    request.Details[0].ActualAmountBsS == 550m &&
                    request.ActualClosingBalanceUsd == 13.25m)), Times.Once);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public async Task SingleProfile_UsesPrimarySymbolForTheStatusAndPrimaryCodeInUsdValidation()
    {
        var previousFormat = CurrencyDisplay.Current;
        var closureService = new Mock<IDailyClosureClientService>();
        var dialogService = new Mock<IDialogService>();
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplySingleEurProfile();
            SetupTotals(closureService);

            var viewModel = CreateViewModel(closureService, dialogService);
            try
            {
                await viewModel.LoadExpectedTotalsCommand.ExecuteAsync(null);
                viewModel.DetailRows[0].ActualAmountBsS = 550m;

                Assert.Equal("SOBRANTE EN CAJA (+50,00 €)", viewModel.DifferenceStatusLabel);

                viewModel.ActualClosingBalanceUsdText = "abc";
                await viewModel.ConfirmClosureCommand.ExecuteAsync(null);

                dialogService.Verify(d => d.ShowWarning(
                    "Advertencia",
                    "Ingrese un conteo EUR válido o deje el campo vacío."), Times.Once);
                closureService.Verify(s => s.CreateClosureAsync(It.IsAny<CreateClosureRequest>()), Times.Never);

                Assert.Equal(500m, viewModel.TotalExpectedBsS);
                Assert.Equal(550m, viewModel.TotalActualBsS);
                Assert.Equal(50m, viewModel.TotalDifferenceBsS);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void Converters_ResolveDailyClosureAmountsAndDualOnlyVisibilityFromTheActiveProfile()
    {
        var previousFormat = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;

            ApplyDualProfile();
            Assert.Equal("Bs.S 550,00", Convert(new LocalAmountConverter(), 550m));
            Assert.Equal("$ 12,50", Convert(new PrimaryAmountConverter(), 12.5m));
            Assert.Equal(Visibility.Visible, Convert(new DualOnlyVisibilityConverter(), true));

            ApplySingleEurProfile();
            Assert.Equal("€ 550,00", Convert(new LocalAmountConverter(), 550m));
            Assert.Equal("€ 12,50", Convert(new PrimaryAmountConverter(), 12.5m));
            Assert.Equal(Visibility.Collapsed, Convert(new DualOnlyVisibilityConverter(), true));
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    private static object Convert(System.Windows.Data.IValueConverter converter, object? value) =>
        converter.Convert(value!, typeof(string), null!, CultureInfo.InvariantCulture);
}
