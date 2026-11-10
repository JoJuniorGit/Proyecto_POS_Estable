using Core.Common;
using Core.Helpers;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3d (F3d): supplier invoice cost normalization. <see cref="SupplierInvoiceLine.UnitCostUSD"/>
/// keeps its legacy name but stores the cost in the profile primary currency: USD/primary documents
/// need no conversion (rate 1), a Dual secondary is divided by the invoice AppliedRate snapshot, and
/// the historical Bs.S path keeps its divide-by-rate behavior.
/// </summary>
public class PricingCalculatorTests
{
    [Fact]
    public void CalculateInvoiceUnitCostUsd_UsdDocument_DoesNotConvert()
    {
        var result = PricingCalculator.CalculateInvoiceUnitCostUsd(1000m, 1m, CurrencyCodes.Usd, 1m);

        Assert.Equal(1000m, result);
    }

    [Fact]
    public void CalculateInvoiceUnitCostUsd_SinglePrimaryDocument_WithIdentityRate_DoesNotConvert()
    {
        // Single COP (or EUR/MXN) stages the document cost in the primary with rate 1; the legacy
        // column name must not trigger a Bs.S-style division.
        var result = PricingCalculator.CalculateInvoiceUnitCostUsd(1000m, 1m, "COP", 1m);

        Assert.Equal(1000m, result);
    }

    [Fact]
    public void CalculateInvoiceUnitCostUsd_DualSecondaryDocument_DividesBySnapshotRate()
    {
        var result = PricingCalculator.CalculateInvoiceUnitCostUsd(1000m, 1m, "COP", 4000m);

        Assert.Equal(0.25m, result);
    }

    [Fact]
    public void CalculateInvoiceUnitCostUsd_DualSecondaryDocument_UsesFourDecimalSnapshot()
    {
        // 1000 / 4000.1235 = 0.249992... -> digital 2dp for the line cost.
        var result = PricingCalculator.CalculateInvoiceUnitCostUsd(1000m, 1m, "COP", 4000.1235m);

        Assert.Equal(0.25m, result);
    }

    [Fact]
    public void CalculateInvoiceUnitCostUsd_BsSDocument_KeepsLegacyDivision()
    {
        var result = PricingCalculator.CalculateInvoiceUnitCostUsd(1000m, 1m, CurrencyCodes.BsS, 50m);

        Assert.Equal(20m, result);
    }

    [Fact]
    public void CalculateInvoiceUnitCostUsd_BsSDocument_WithIdentityRate_DoesNotConvert()
    {
        // Single VES uses rate 1; the cost is already expressed in the primary (legacy Bs.S label).
        var result = PricingCalculator.CalculateInvoiceUnitCostUsd(1000m, 1m, CurrencyCodes.BsS, 1m);

        Assert.Equal(1000m, result);
    }

    [Fact]
    public void CalculateInvoiceUnitCostUsd_ConversionFactor_IsAppliedBeforeCurrencyConversion()
    {
        // 1200 Bs.S per box / 3 units = 400 Bs.S per unit -> 8 USD at rate 50.
        var result = PricingCalculator.CalculateInvoiceUnitCostUsd(1200m, 3m, CurrencyCodes.BsS, 50m);

        Assert.Equal(8m, result);
    }
}
