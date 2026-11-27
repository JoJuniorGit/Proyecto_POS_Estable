using System;
using System.Collections.Generic;
using System.Text;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Sales.Module.Entities;
using Sales.Module.Receipts;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T4a (S10/F4a): PDF de venta y CxC con moneda primaria/secundaria persistida. Dual
/// muestra ambas etiquetas; Single muestra una sola y jamás una línea secundaria. Ningún
/// comprobante hardcodea Bs.S/USD para el régimen activo.
/// </summary>
public class DualSecondaryCurrencyReceiptTests
{
    private static MonetaryProfileDto DualUsdCopProfile(string secondarySymbol = "COP") => new()
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "COP",
        SecondaryCurrencySymbol = secondarySymbol,
        IsConfigured = true
    };

    private static MonetaryProfileDto SingleCopProfile() => new()
    {
        Mode = MonetaryMode.Single,
        PrimaryCurrencyCode = "COP",
        PrimaryCurrencySymbol = "COP",
        SecondaryCurrencyCode = null,
        SecondaryCurrencySymbol = null,
        IsConfigured = true
    };

    private static Sale BuildSale(
        string primaryCode,
        string? secondaryCode,
        string mode,
        decimal totalUsd,
        decimal totalSecondary,
        decimal rate,
        int id = 950)
    {
        var sale = new Sale
        {
            Id = id,
            InvoiceNumber = 95000 + id,
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
            PaymentMethod = new PaymentMethod { Name = "Pago Moneda", IsCash = false },
            Amount = totalUsd,
            AmountBsS = totalSecondary,
            ExchangeRate = rate
        });
        return sale;
    }

    private static string BuildSaleReceiptText(SaleReceiptContext context)
        => Encoding.ASCII.GetString(
            SaleReceiptPdfGenerator.BuildPdf(context, ReceiptDocumentKind.NonFiscalSaleReceipt, MoneyDisplayFormat.Venezuelan));

    [Fact]
    public void DualUsdCopSaleReceipt_ShowsBothCurrenciesWithoutHardcodedBsS()
    {
        var sale = BuildSale("USD", "COP", "Dual", totalUsd: 10m, totalSecondary: 40000m, rate: 4000m);
        var context = SaleReceiptContext.CreateFrom(sale, DualUsdCopProfile());

        string content = BuildSaleReceiptText(context);

        Assert.Contains("Total USD:", content);
        Assert.Contains("COP", content);
        Assert.Contains("$", content);
        Assert.DoesNotContain("Bs.S", content);
    }

    [Fact]
    public void SingleCopSaleReceipt_ShowsOneCurrencyAndNoSecondaryLine()
    {
        var sale = BuildSale("COP", null, "Single", totalUsd: 100m, totalSecondary: 100m, rate: 1m);
        var context = SaleReceiptContext.CreateFrom(sale, SingleCopProfile());

        string content = BuildSaleReceiptText(context);

        Assert.Contains("Total COP:", content);
        Assert.Contains("COP", content);
        Assert.DoesNotContain("Bs.S", content);
        Assert.DoesNotContain("Total USD:", content);
        Assert.DoesNotContain("Tasa aplicada", content);
    }

    [Fact]
    public void LegacyVesSaleReceipt_StillShowsBsS()
    {
        var sale = BuildSale("USD", "VES", "Dual", totalUsd: 10m, totalSecondary: 36500m, rate: 3650m);
        var context = SaleReceiptContext.CreateFrom(sale);

        string content = BuildSaleReceiptText(context);

        Assert.Contains("Bs.S", content);
        Assert.Contains("Total USD:", content);
        Assert.DoesNotContain("COP", content);
    }

    [Fact]
    public void NonAsciiConfiguredSymbol_FallsBackToIsoInPdf_NeverQuestionMark()
    {
        var sale = BuildSale("USD", "EUR", "Dual", totalUsd: 10m, totalSecondary: 9245m, rate: 9245m);
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

        string content = BuildSaleReceiptText(context);

        Assert.Contains("EUR", content);
        Assert.DoesNotContain("?", content);
    }

    [Fact]
    public void CreditSaleReceipt_UsesSecondaryCurrencyLabel()
    {
        var sale = BuildSale("USD", "COP", "Dual", totalUsd: 100m, totalSecondary: 400000m, rate: 4000m, id: 951);
        var receivable = new Receivable
        {
            Id = 77,
            SaleId = sale.Id,
            OriginalUSD = 80m,
            BalanceUSD = 80m,
            OriginalBsS = 320000m,
            HistoricalRate = 4000m,
            IssuedAtUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
            DueDateUtc = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc)
        };

        var context = CreditSaleReceiptContext.CreateFrom(sale, receivable, DualUsdCopProfile());
        var content = Encoding.ASCII.GetString(
            CreditSaleReceiptPdfGenerator.BuildPdf(context, MoneyDisplayFormat.Venezuelan));

        Assert.Equal("COP", context.SecondaryCurrencyCode);
        Assert.Equal("COP", context.SecondaryCurrencySymbol);
        Assert.Contains("COP", content);
        Assert.DoesNotContain("Bs.S", content);
    }

    [Fact]
    public void CreditPaymentReceipt_UsesFirstAllocationSaleSnapshotsAndProfileLabels()
    {
        var sale = BuildSale("USD", "COP", "Dual", totalUsd: 100m, totalSecondary: 400000m, rate: 4000m, id: 952);
        var receivable = new Receivable
        {
            Id = 88,
            SaleId = sale.Id,
            Sale = sale,
            OriginalUSD = 100m,
            BalanceUSD = 60m,
            OriginalBsS = 400000m,
            HistoricalRate = 4000m,
            IssuedAtUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
            DueDateUtc = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc)
        };
        var payment = new ReceivablePayment
        {
            Id = 55,
            CustomerId = 1,
            Customer = new Customer { Name = "Cliente CxC" },
            PaymentRate = 4000m,
            AmountUSD = 40m,
            AmountBsS = 160000m,
            FxDifferenceBsS = 0m,
            ReceivedAtUtc = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            Allocations =
            {
                new CreditPaymentAllocation
                {
                    Id = 1,
                    ReceivableId = receivable.Id,
                    Receivable = receivable,
                    AmountUSD = 40m,
                    AmountBsS = 160000m
                }
            }
        };

        var context = CreditPaymentReceiptContext.CreateFrom(
            payment,
            currentDebtUsd: 60m,
            todayRate: 4000m,
            laterAllocationsByReceivable: new Dictionary<int, decimal>(),
            profile: DualUsdCopProfile());
        var content = Encoding.ASCII.GetString(
            CreditPaymentReceiptPdfGenerator.BuildPdf(context, MoneyDisplayFormat.Venezuelan));

        Assert.Equal("USD", context.PrimaryCurrencyCode);
        Assert.Equal("COP", context.SecondaryCurrencyCode);
        Assert.Contains("COP", content);
        Assert.DoesNotContain("Bs.S", content);
    }
}
