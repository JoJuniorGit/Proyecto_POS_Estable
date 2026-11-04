using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
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
/// 8.211-T4c2b3b (S9): the cash drawer view models and presentation converters resolve amounts
/// and labels from the active monetary profile. Dual keeps the primary/secondary pair; Single
/// collapses every local amount to the primary currency and hides the duplicate line. Raw
/// decimal properties, calculations and commands stay unchanged.
/// </summary>
public class CashDrawerMonetaryProfileDisplayTests
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

    private static CashDrawerViewModel CreateDrawer()
    {
        var cash = new Mock<ICashDrawerService>();
        cash.Setup(s => s.GetActiveSessionAsync()).ReturnsAsync((CashDrawerSessionDto?)null);
        cash.Setup(s => s.GetHistoryAsync(It.IsAny<int>())).ReturnsAsync(new List<CashTransactionDto>());

        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(r => r.CurrentRate).Returns(50m);

        var session = new UserSession();
        var viewModel = new CashDrawerViewModel(
            cash.Object,
            rate.Object,
            new Mock<IDialogService>().Object,
            new Mock<IPaymentService>().Object,
            session);
        // The global messenger is shared across tests; isolate this instance.
        WeakReferenceMessenger.Default.UnregisterAll(viewModel);

        // Registering the user after construction suppresses the initial background load.
        session.SetUser(new UserDto { Id = 1, Name = "Supervisor Test", Cedula = "V-1", Role = UserRole.Admin });
        return viewModel;
    }

    private static void SeedCashDrawer(CashDrawerViewModel viewModel)
    {
        viewModel.ActiveSession = new CashDrawerSessionDto
        {
            Id = 1,
            Status = CashDrawerStatus.Open,
            Transactions = new List<CashTransactionDto>
            {
                new() { Type = CashTransactionType.Income, Source = CashTransactionSource.CashIn, IsPhysicalCash = true, AmountLocal = 500m },
                new() { Type = CashTransactionType.Expense, Source = CashTransactionSource.CashOut, IsPhysicalCash = true, AmountLocal = 100m }
            }
        };
        viewModel.PettyCashPhysicalBalanceBsS = 1500.25m;
        viewModel.PettyCashPhysicalBalanceUsd = 32.5m;
        viewModel.PettyCashExpenses.Add(new PettyCashExpenseRowViewModel(new PettyCashExpenseClientDto
        {
            Id = 7,
            VoucherNumber = 7,
            SessionId = 1,
            Category = PettyCashCategoryClient.InsumosLimpieza,
            Currency = PettyCashCurrencyClient.BsS,
            AmountBsS = 1000m,
            AmountUsd = 20m,
            HistoricalRate = 50m,
            Concept = "Detergente",
            Beneficiary = "Limpieza Total",
            Status = PettyCashStatusClient.Active
        }));

        // Updating the balance recalculates the formatted balance and session totals.
        viewModel.CurrentBalanceBsS = 1300m;
    }

    [Fact]
    public void DualProfile_FormatsLocalAndPrimaryDisplaysAndKeepsRawDecimals()
    {
        var previousFormat = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplyDualProfile();
            var viewModel = CreateDrawer();
            SeedCashDrawer(viewModel);

            Assert.Equal("1.300", viewModel.FormattedBalanceBsS);
            Assert.Equal("26,00 $", viewModel.FormattedBalanceUsd);
            Assert.Equal("Bs.S 500", viewModel.FormattedTotalIncomeBsS);
            Assert.Equal("Bs.S 100", viewModel.FormattedTotalExpenseBsS);
            Assert.Equal("Bs.S 1.500,25", viewModel.PettyCashPhysicalBalanceBsSDisplay);
            Assert.Equal("$ 32,50", viewModel.PettyCashPhysicalBalanceUsdDisplay);
            Assert.Equal("1 activo(s) · Bs.S 1.000,00 · $ 20,00", viewModel.PettyCashSummary);

            Assert.Equal(1300m, viewModel.CurrentBalanceBsS);
            Assert.Equal(500m, viewModel.TotalIncomeBsS);
            Assert.Equal(100m, viewModel.TotalExpenseBsS);
            Assert.Equal(1500.25m, viewModel.PettyCashPhysicalBalanceBsS);
            Assert.Equal(32.5m, viewModel.PettyCashPhysicalBalanceUsd);
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void SingleEurProfile_FormatsLocalAmountsInPrimaryAndCollapsesTheDuplicateLine()
    {
        var previousFormat = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplySingleEurProfile();
            var viewModel = CreateDrawer();
            SeedCashDrawer(viewModel);

            Assert.Equal("1.300", viewModel.FormattedBalanceBsS);
            Assert.Equal("26,00 €", viewModel.FormattedBalanceUsd);
            Assert.Equal("€ 500", viewModel.FormattedTotalIncomeBsS);
            Assert.Equal("€ 100", viewModel.FormattedTotalExpenseBsS);
            Assert.Equal("€ 1.500,25", viewModel.PettyCashPhysicalBalanceBsSDisplay);
            Assert.Equal("€ 32,50", viewModel.PettyCashPhysicalBalanceUsdDisplay);
            Assert.Equal("1 activo(s) · € 1.000,00", viewModel.PettyCashSummary);

            Assert.Equal(1300m, viewModel.CurrentBalanceBsS);
            Assert.Equal(500m, viewModel.TotalIncomeBsS);
            Assert.Equal(100m, viewModel.TotalExpenseBsS);
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void CashAdvanceValidationMessage_UsesTheLocalCurrencyOfTheProfile()
    {
        var previousFormat = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplySingleEurProfile();
            var viewModel = CreateAdvanceDialog(availableCashLocal: 5000m);

            viewModel.RequestedAmountBsS = 6000m;

            Assert.Equal("El monto supera el efectivo en caja (5.000,00 EUR).", viewModel.ErrorMessage);
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void CashAdvanceValidationMessage_KeepsTheConfiguredLocalLabelInDual()
    {
        var previousFormat = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplyDualProfile();
            var viewModel = CreateAdvanceDialog(availableCashLocal: 5000m);

            viewModel.RequestedAmountBsS = 6000m;

            Assert.Equal("El monto supera el efectivo en caja (5.000,00 Bs.S).", viewModel.ErrorMessage);
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void Converters_ResolveCashDrawerAmountsFromTheActiveProfile()
    {
        var previousFormat = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;

            ApplyDualProfile();
            Assert.Equal("Bs.S 3.500,25", Convert(new LocalAmountConverter(), 3500.25m));
            Assert.Equal("$ 1.250,50", Convert(new PrimaryAmountConverter(), 1250.50m));
            Assert.Equal(Visibility.Visible, Convert(new DualOnlyVisibilityConverter(), true));

            ApplySingleEurProfile();
            Assert.Equal("€ 3.500,25", Convert(new LocalAmountConverter(), 3500.25m));
            Assert.Equal("€ 1.250,50", Convert(new PrimaryAmountConverter(), 1250.50m));
            Assert.Equal(Visibility.Collapsed, Convert(new DualOnlyVisibilityConverter(), true));
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    private static CashAdvanceRegisterViewModel CreateAdvanceDialog(decimal? availableCashLocal)
    {
        var methods = new List<PaymentMethodDto>
        {
            new() { Id = 1, Name = "Efectivo", IsCash = true, DisplayOrder = 1 },
            new() { Id = 2, Name = "Transferencia", IsCash = false, DisplayOrder = 2 }
        };

        return new CashAdvanceRegisterViewModel(methods, availableCashLocal, 50m);
    }

    private static object Convert(System.Windows.Data.IValueConverter converter, object? value) =>
        converter.Convert(value!, typeof(string), null!, CultureInfo.InvariantCulture);
}
