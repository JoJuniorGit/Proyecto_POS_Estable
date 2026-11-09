using System.Collections.Generic;
using Core.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// S1/S2: resumen físico por moneda de la sesión. La moneda local va primero; los ingresos
/// excluyen apertura y reversas de caja chica; los egresos son netos de reversas y excluyen el
/// cierre. Cada fila se expresa en su propia divisa, sin conversiones cruzadas.
/// </summary>
public class CashDrawerCurrencySummaryTests
{
    private static MonetaryProfileDto DualUsdVes() => new()
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "VES",
        SecondaryCurrencySymbol = "Bs.S"
    };

    private static MonetaryProfileDto SingleCop() => new()
    {
        Mode = MonetaryMode.Single,
        PrimaryCurrencyCode = "COP",
        PrimaryCurrencySymbol = "COP",
        SecondaryCurrencyCode = null,
        SecondaryCurrencySymbol = null
    };

    private static CashTransaction Tx(
        CashTransactionType type,
        CashTransactionSource source,
        string currency,
        decimal amountLocal,
        decimal amountUsd,
        decimal rate = 50m) => new()
    {
        Type = type,
        Source = source,
        CurrencyCode = currency,
        AmountLocal = amountLocal,
        AmountUsd = amountUsd,
        ExchangeRate = rate,
        IsPhysicalCash = true
    };

    [Fact]
    public void Dual_BuildsLocalFirstAndNetsPettyCashReversals()
    {
        var session = new CashDrawerSession
        {
            OpeningBalanceLocal = 941.235m,
            OpeningBalanceUsd = 0m,
            Transactions = new List<CashTransaction>
            {
                Tx(CashTransactionType.Income, CashTransactionSource.Opening, "VES", 941.235m, 18.8247m),
                Tx(CashTransactionType.Income, CashTransactionSource.SalePayment, "VES", 49.214m, 1.10352m, 44.59m),
                Tx(CashTransactionType.Income, CashTransactionSource.SalePayment, "USD", 500m, 10m),
                Tx(CashTransactionType.Expense, CashTransactionSource.CashOut, "VES", 5m, 0.1m),
                Tx(CashTransactionType.Expense, CashTransactionSource.PettyCashExpense, "VES", 2m, 0.04m),
                Tx(CashTransactionType.Income, CashTransactionSource.PettyCashReversal, "VES", 1m, 0.02m),
                Tx(CashTransactionType.Expense, CashTransactionSource.Closing, "VES", 100m, 2m)
            }
        };

        var rows = CashDrawerCurrencySummaryBuilder.Build(session, DualUsdVes());

        Assert.Collection(
            rows,
            local =>
            {
                Assert.Equal("VES", local.CurrencyCode);
                Assert.Equal("Bs.S", local.CurrencySymbol);
                Assert.True(local.IsLocal);
                Assert.Equal(941.235m, local.OpeningBalance);
                Assert.Equal(49.214m, local.TotalPhysicalInflows);
                Assert.Equal(6m, local.TotalPhysicalOutflows);
                Assert.Equal(984.449m, local.ExpectedCashBalance);
            },
            primary =>
            {
                Assert.Equal("USD", primary.CurrencyCode);
                Assert.Equal("$", primary.CurrencySymbol);
                Assert.False(primary.IsLocal);
                Assert.Equal(0m, primary.OpeningBalance);
                Assert.Equal(10m, primary.TotalPhysicalInflows);
                Assert.Equal(0m, primary.TotalPhysicalOutflows);
                Assert.Equal(10m, primary.ExpectedCashBalance);
            });
    }

    [Fact]
    public void Single_BuildsOnlyThePrimaryRowEvenWithLegacyOtherCurrencyMovements()
    {
        var session = new CashDrawerSession
        {
            OpeningBalanceLocal = 100m,
            OpeningBalanceUsd = 0m,
            Transactions = new List<CashTransaction>
            {
                Tx(CashTransactionType.Income, CashTransactionSource.SalePayment, "COP", 20m, 0.4m),
                Tx(CashTransactionType.Expense, CashTransactionSource.CashOut, "COP", 5m, 0.1m),
                Tx(CashTransactionType.Income, CashTransactionSource.SalePayment, "USD", 500m, 10m)
            }
        };

        var rows = CashDrawerCurrencySummaryBuilder.Build(session, SingleCop());

        var row = Assert.Single(rows);
        Assert.Equal("COP", row.CurrencyCode);
        Assert.True(row.IsLocal);
        Assert.Equal(100m, row.OpeningBalance);
        Assert.Equal(20m, row.TotalPhysicalInflows);
        Assert.Equal(5m, row.TotalPhysicalOutflows);
        Assert.Equal(115m, row.ExpectedCashBalance);
    }

    [Fact]
    public void Primary_AmountFallsBackToTheTransactionRateWhenAmountUsdIsMissing()
    {
        var session = new CashDrawerSession
        {
            OpeningBalanceLocal = 0m,
            OpeningBalanceUsd = 0m,
            Transactions = new List<CashTransaction>
            {
                Tx(CashTransactionType.Income, CashTransactionSource.SalePayment, "USD", 500m, 0m, 50m)
            }
        };

        var rows = CashDrawerCurrencySummaryBuilder.Build(session, DualUsdVes());

        Assert.Equal(10m, rows[1].TotalPhysicalInflows);
        Assert.Equal(10m, rows[1].ExpectedCashBalance);
    }
}
