using System;
using System.Text;
using Core.DTOs;
using Core.Helpers;
using Sales.Module.Entities;
using Sales.Module.Receipts;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T4a (S10/F4a): presentación monetaria de los comprobantes backend. Los códigos salen de
/// los snapshots persistidos de la venta; el símbolo configurado del perfil solo se usa cuando su
/// código coincide y es representable en la fuente PDF (ASCII imprimible). Un símbolo Unicode
/// (p. ej. "€") cae al código ISO y nunca a "?".
/// </summary>
public class ReceiptCurrencyFormattingTests
{
    private static Sale BuildSale(
        string primaryCode = "USD",
        string? secondaryCode = "VES",
        string mode = "Dual",
        decimal totalUsd = 10m,
        decimal totalSecondary = 40000m,
        decimal rate = 4000m,
        int id = 900,
        string? paymentCurrencyCode = null)
    {
        var sale = new Sale
        {
            Id = id,
            InvoiceNumber = 90000 + id,
            Date = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
            CustomerName = "Cliente Moneda",
            CustomerCedula = "V-12345678",
            Subtotal = totalUsd,
            TotalUSD = totalUsd,
            TotalBsS = totalSecondary,
            SubtotalBsS = totalSecondary,
            AppliedRate = rate,
            RoundingAdjustment = 0m,
            FinalPaidAmountBsS = totalSecondary,
            PrimaryCurrencyCode = primaryCode,
            SecondaryCurrencyCode = secondaryCode,
            MonetaryMode = mode
        };
        sale.Items.Add(new SaleItem
        {
            ProductName = "Producto Moneda",
            Quantity = 1m,
            UnitPrice = totalUsd,
            Subtotal = totalUsd,
            UnitPriceBsS = totalSecondary,
            SubtotalBsS = totalSecondary
        });
        sale.Payments.Add(new SalePayment
        {
            PaymentMethod = new PaymentMethod { Name = "Efectivo", IsCash = true },
            Amount = totalUsd,
            AmountBsS = totalSecondary,
            ExchangeRate = rate,
            CurrencyCode = paymentCurrencyCode
        });
        return sale;
    }

    private static MonetaryProfileDto DualUsdCopProfile(string secondarySymbol = "COP") => new()
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "COP",
        SecondaryCurrencySymbol = secondarySymbol,
        IsConfigured = true
    };

    private static MonetaryProfileDto SingleCopProfile(string symbol = "COP") => new()
    {
        Mode = MonetaryMode.Single,
        PrimaryCurrencyCode = "COP",
        PrimaryCurrencySymbol = symbol,
        SecondaryCurrencyCode = null,
        SecondaryCurrencySymbol = null,
        IsConfigured = true
    };

    [Theory]
    [InlineData("VES", "Bs.S")]
    [InlineData("Bs.S", "Bs.S")]
    [InlineData("BsS", "Bs.S")]
    [InlineData("USD", "USD")]
    [InlineData("COP", "COP")]
    [InlineData("EUR", "EUR")]
    public void PresentationCode_NormalizesVesAliasAndKeepsIsoCodes(string code, string expected)
    {
        Assert.Equal(expected, ReceiptCurrencyFormatter.PresentationCode(code));
    }

    [Fact]
    public void ResolveSymbol_UsesConfiguredAsciiSymbolAndKnownDefaults()
    {
        Assert.Equal("US$", ReceiptCurrencyFormatter.ResolveSymbol("USD", "US$"));
        Assert.Equal("$", ReceiptCurrencyFormatter.ResolveSymbol("USD", null));
        Assert.Equal("Bs.S", ReceiptCurrencyFormatter.ResolveSymbol("VES", null));
        Assert.Equal("COP", ReceiptCurrencyFormatter.ResolveSymbol("COP", null));
    }

    [Fact]
    public void ResolveSymbol_NonAsciiSymbolFallsBackToIsoCodeNeverQuestionMark()
    {
        string label = ReceiptCurrencyFormatter.ResolveSymbol("EUR", "€");

        Assert.Equal("EUR", label);
        Assert.DoesNotContain("?", label);
    }

    [Fact]
    public void ResolveSymbol_ControlCharactersFallBackToIsoCode()
    {
        string label = ReceiptCurrencyFormatter.ResolveSymbol("COP", "CO\nP");

        Assert.Equal("COP", label);
    }

    [Fact]
    public void CreateFrom_DualUsdCop_UsesSnapshotCodesAndProfileSymbols()
    {
        var sale = BuildSale(primaryCode: "USD", secondaryCode: "COP", mode: "Dual");

        var context = SaleReceiptContext.CreateFrom(sale, DualUsdCopProfile("COL$"));

        Assert.Equal("USD", context.PrimaryCurrencyCode);
        Assert.Equal("$", context.PrimaryCurrencySymbol);
        Assert.Equal("COP", context.SecondaryCurrencyCode);
        Assert.Equal("COL$", context.SecondaryCurrencySymbol);
        Assert.Equal("Dual", context.MonetaryMode);
        Assert.False(context.IsSingleCurrency);
    }

    [Fact]
    public void CreateFrom_SingleCop_HasNoSecondaryCurrency()
    {
        var sale = BuildSale(
            primaryCode: "COP",
            secondaryCode: null,
            mode: "Single",
            totalUsd: 100m,
            totalSecondary: 100m,
            rate: 1m);

        var context = SaleReceiptContext.CreateFrom(sale, SingleCopProfile());

        Assert.Equal("COP", context.PrimaryCurrencyCode);
        Assert.Equal("COP", context.PrimaryCurrencySymbol);
        Assert.Null(context.SecondaryCurrencyCode);
        Assert.Null(context.SecondaryCurrencySymbol);
        Assert.Equal("Single", context.MonetaryMode);
        Assert.True(context.IsSingleCurrency);
    }

    [Fact]
    public void CreateFrom_LegacySaleWithoutProfile_DefaultsToUsdVesDual()
    {
        var sale = BuildSale();
        sale.PrimaryCurrencyCode = string.Empty;
        sale.SecondaryCurrencyCode = null;
        sale.MonetaryMode = string.Empty;

        var context = SaleReceiptContext.CreateFrom(sale);

        Assert.Equal("USD", context.PrimaryCurrencyCode);
        Assert.Equal("$", context.PrimaryCurrencySymbol);
        Assert.Equal("VES", context.SecondaryCurrencyCode);
        Assert.Equal("Bs.S", context.SecondaryCurrencySymbol);
        Assert.Equal("Dual", context.MonetaryMode);
    }

    [Fact]
    public void CreateFrom_LegacySaleWithSingleCopProfile_StillUsesHistoricalUsdVesCodes()
    {
        var sale = BuildSale();
        sale.PrimaryCurrencyCode = string.Empty;
        sale.SecondaryCurrencyCode = null;
        sale.MonetaryMode = string.Empty;

        var context = SaleReceiptContext.CreateFrom(sale, SingleCopProfile());

        // La fila legacy se registró en USD/VES: el perfil actual solo aporta símbolos cuando el
        // código coincide, nunca reinterpreta el snapshot histórico.
        Assert.Equal("USD", context.PrimaryCurrencyCode);
        Assert.Equal("$", context.PrimaryCurrencySymbol);
        Assert.Equal("VES", context.SecondaryCurrencyCode);
        Assert.Equal("Bs.S", context.SecondaryCurrencySymbol);
        Assert.Equal("Dual", context.MonetaryMode);
    }

    [Fact]
    public void CreateFrom_ProfileSymbolDoesNotLeakAcrossDifferentCodes()
    {
        var sale = BuildSale(
            primaryCode: "EUR",
            secondaryCode: null,
            mode: "Single",
            totalUsd: 100m,
            totalSecondary: 100m,
            rate: 1m);

        var context = SaleReceiptContext.CreateFrom(sale, DualUsdCopProfile());

        Assert.Equal("EUR", context.PrimaryCurrencySymbol);
    }

    [Fact]
    public void CreateFrom_ProfileNonAsciiSecondarySymbol_FallsBackToIsoCode()
    {
        var sale = BuildSale(
            primaryCode: "USD",
            secondaryCode: "EUR",
            mode: "Dual",
            totalSecondary: 9245m,
            rate: 9245m);
        var profile = new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = "EUR",
            SecondaryCurrencySymbol = "€",
            IsConfigured = true
        };

        var context = SaleReceiptContext.CreateFrom(sale, profile);

        Assert.Equal("EUR", context.SecondaryCurrencyCode);
        Assert.Equal("EUR", context.SecondaryCurrencySymbol);
        Assert.DoesNotContain("?", context.SecondaryCurrencySymbol);
    }

    [Fact]
    public void SaleReceiptPdf_DualUsdCop_UsdTenderUsesPrimarySymbolNotSecondary()
    {
        var sale = BuildSale(paymentCurrencyCode: "USD");
        var context = SaleReceiptContext.CreateFrom(sale, DualUsdCopProfile());

        var content = Encoding.ASCII.GetString(
            SaleReceiptPdfGenerator.BuildPdf(context, ReceiptDocumentKind.NonFiscalSaleReceipt, MoneyDisplayFormat.International));

        Assert.Contains("Efectivo: $ 10.00", content);
        Assert.DoesNotContain("Efectivo: COP", content);
    }

    [Fact]
    public void PettyCashVoucherPdf_SingleCop_HidesUsdEquivalent()
    {
        var expense = new PettyCashExpense
        {
            VoucherNumber = 1,
            SessionId = 1,
            Category = PettyCashCategory.Varios,
            Currency = PettyCashCurrency.BsS,
            AmountBsS = 1000m,
            AmountUsd = 1000m,
            HistoricalRate = 1m,
            Concept = "Compra operativa",
            Beneficiary = "Proveedor",
            ReferenceNumber = "REF-1",
            CreatedByUserId = 1,
            CreatedAtUtc = DateTime.UtcNow,
            Status = PettyCashStatus.Active
        };
        var profile = new MonetaryProfileDto
        {
            Mode = MonetaryMode.Single,
            PrimaryCurrencyCode = "COP",
            PrimaryCurrencySymbol = "COP",
            SecondaryCurrencyCode = null,
            SecondaryCurrencySymbol = null,
            IsConfigured = true
        };

        var context = PettyCashVoucherContext.CreateFrom(
            expense,
            "Cajero",
            authorizerName: null,
            profile: profile,
            persistedCurrencyCode: "COP");

        Assert.False(context.IncludesUsd);
        var content = Encoding.ASCII.GetString(
            PettyCashVoucherPdfGenerator.BuildPdf(context, MoneyDisplayFormat.International));
        Assert.Contains("Equivalente: COP", content);
        Assert.DoesNotContain(" / $", content);
    }
}
