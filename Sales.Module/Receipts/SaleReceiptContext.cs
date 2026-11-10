using System;
using Core.DTOs;
using Sales.Module.Entities;

namespace Sales.Module.Receipts;

public sealed record ReceiptLine(
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal Subtotal,
    decimal UnitPriceBsS,
    decimal SubtotalBsS);

public sealed record ReceiptPayment(
    string? MethodName,
    decimal Amount,
    decimal AmountBsS,
    decimal ExchangeRate,
    string? ReferenceNumber,
    string? CurrencyCode = null);

public sealed class SaleReceiptContext
{
    public int SaleId { get; init; }
    public int? InvoiceNumber { get; init; }
    public DateTime Date { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerCedula { get; init; }
    public decimal Subtotal { get; init; }
    public decimal TotalUSD { get; init; }

    /// <summary>
    /// Sincronizacion USD efectivo (ANEXO XIII): inverso del Bs.S EFECTIVO a la tasa anclada.
    /// El PDF imprime este valor (no TotalUSD) para que el $ del recibo sea el monto realmente
    /// transado y cierre con el Bs.S cobrado.
    /// </summary>
    public decimal EffectiveTotalUSD { get; init; }

    public decimal TotalBsS { get; init; }
    public decimal AppliedRate { get; init; }
    public decimal RoundingAdjustment { get; init; }
    public decimal FinalPaidAmountBsS { get; init; }

    // 8.211-T4a (S10/F4a): códigos persistidos de la venta y etiquetas seguras del perfil.
    // Legacy/sin perfil conserva USD/$ y VES/Bs.S; Single no tiene moneda secundaria.
    public string PrimaryCurrencyCode { get; init; } = ReceiptCurrencyFormatter.UsdCode;
    public string PrimaryCurrencySymbol { get; init; } = "$";
    public string? SecondaryCurrencyCode { get; init; } = ReceiptCurrencyFormatter.VesCode;
    public string? SecondaryCurrencySymbol { get; init; } = ReceiptCurrencyFormatter.VesPresentationCode;
    public string MonetaryMode { get; init; } = ReceiptCurrencyFormatter.DualMode;

    public bool IsSingleCurrency => string.Equals(MonetaryMode, ReceiptCurrencyFormatter.SingleMode, StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(SecondaryCurrencyCode);

    public IReadOnlyList<ReceiptLine> Lines { get; init; } = Array.Empty<ReceiptLine>();
    public IReadOnlyList<ReceiptPayment> Payments { get; init; } = Array.Empty<ReceiptPayment>();

    public static SaleReceiptContext CreateFrom(Sale sale, MonetaryProfileDto? profile = null)
    {
        ArgumentNullException.ThrowIfNull(sale);

        string mode = ReceiptCurrencyFormatter.NormalizeMode(sale.MonetaryMode, profile: null);
        var primary = ReceiptCurrencyFormatter.ResolveCurrency(
            sale.PrimaryCurrencyCode,
            ReceiptCurrencyFormatter.UsdCode,
            profile);
        var secondary = string.Equals(mode, ReceiptCurrencyFormatter.SingleMode, StringComparison.Ordinal)
            ? null
            : ReceiptCurrencyFormatter.ResolveCurrency(
                sale.SecondaryCurrencyCode,
                ReceiptCurrencyFormatter.VesCode,
                profile);

        return new SaleReceiptContext
        {
            PrimaryCurrencyCode = primary.Code,
            PrimaryCurrencySymbol = primary.Symbol,
            SecondaryCurrencyCode = secondary?.Code,
            SecondaryCurrencySymbol = secondary?.Symbol,
            MonetaryMode = mode,
            SaleId = sale.Id,
            InvoiceNumber = sale.InvoiceNumber,
            Date = sale.Date,
            CustomerName = sale.CustomerName,
            CustomerCedula = sale.CustomerCedula,
            Subtotal = sale.Subtotal,
            TotalUSD = sale.TotalUSD,
            EffectiveTotalUSD = sale.EffectiveTotalUSD,
            TotalBsS = sale.TotalBsS,
            AppliedRate = sale.AppliedRate,
            RoundingAdjustment = sale.RoundingAdjustment,
            FinalPaidAmountBsS = sale.FinalPaidAmountBsS,
            Lines = sale.Items.Select(i => new ReceiptLine(
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.Subtotal,
                i.UnitPriceBsS,
                i.SubtotalBsS)).ToList(),
            Payments = sale.Payments.Select(p => new ReceiptPayment(
                p.PaymentMethod?.Name,
                p.Amount,
                p.AmountBsS,
                p.ExchangeRate,
                p.ReferenceNumber,
                p.CurrencyCode)).ToList()
        };
    }
}