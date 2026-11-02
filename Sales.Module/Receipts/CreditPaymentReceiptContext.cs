using System;
using System.Collections.Generic;
using System.Linq;
using Core.DTOs;
using Core.Helpers;
using Sales.Module.Entities;

namespace Sales.Module.Receipts;

/// <summary>
/// Línea del comprobante de abono: saldo anterior, monto aplicado (USD y Bs.S) y nuevo saldo de
/// la obligación. Los saldos se reconstruyen con las imputaciones posteriores para que el "nuevo
/// saldo" sea el de la fecha del cobro y no el saldo corriente al momento de imprimir.
/// </summary>
public sealed record CreditPaymentReceiptLine(
    int ReceivableId,
    int SaleId,
    int? InvoiceNumber,
    decimal BalanceBeforeUSD,
    decimal AmountUSD,
    decimal AmountBsS,
    decimal FxDifferenceBsS,
    decimal BalanceAfterUSD);

/// <summary>
/// T3a (F2b, S5/S6): contexto del comprobante de abono CxC. Muestra al cliente el saldo anterior
/// y el nuevo saldo por factura, el monto abonado en USD y su contravalor en Bs.S a la tasa del
/// abono, el saldo pendiente del cliente en USD con su equivalente Bs.S a la tasa del día y el
/// diferencial cambiario realizado. Sin persistencia: se arma on-demand desde el abono.
/// </summary>
public sealed class CreditPaymentReceiptContext
{
    public int PaymentId { get; init; }
    public int CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerCedula { get; init; }
    public DateTime ReceivedAtUtc { get; init; }

    /// <summary>Tasa BCV congelada en el abono (snapshot inmutable).</summary>
    public decimal PaymentRate { get; init; }

    /// <summary>Tasa efectiva del día al imprimir (proyección Bs.S del saldo pendiente).</summary>
    public decimal TodayRate { get; init; }

    public string? PaymentMethodName { get; init; }
    public string? ReferenceNumber { get; init; }
    public decimal TotalAmountUSD { get; init; }
    public decimal TotalAmountBsS { get; init; }
    public decimal FxDifferenceBsS { get; init; }

    /// <summary>Saldo pendiente del cliente (todas sus obligaciones abiertas) al imprimir, en USD.</summary>
    public decimal CurrentDebtUSD { get; init; }

    /// <summary>Equivalente Bs.S del saldo pendiente a <see cref="TodayRate"/> (2dp).</summary>
    public decimal CurrentDebtTodayBsS { get; init; }

    // 8.211-T4a (S10/F4a): la primera imputación aporta el snapshot de la venta; el perfil aporta
    // los símbolos. Sin snapshot ni perfil se conserva el default histórico USD/VES/Dual.
    public string PrimaryCurrencyCode { get; init; } = ReceiptCurrencyFormatter.UsdCode;
    public string PrimaryCurrencySymbol { get; init; } = "$";
    public string? SecondaryCurrencyCode { get; init; } = ReceiptCurrencyFormatter.VesCode;
    public string? SecondaryCurrencySymbol { get; init; } = ReceiptCurrencyFormatter.VesPresentationCode;
    public string MonetaryMode { get; init; } = ReceiptCurrencyFormatter.DualMode;

    public bool IsSingleCurrency => string.Equals(MonetaryMode, ReceiptCurrencyFormatter.SingleMode, StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(SecondaryCurrencyCode);

    public IReadOnlyList<CreditPaymentReceiptLine> Lines { get; init; } = Array.Empty<CreditPaymentReceiptLine>();

    /// <summary>
    /// Arma el contexto desde el abono persistido. <paramref name="laterAllocationsByReceivable"/>
    /// suma las imputaciones posteriores por obligación, necesaria para reconstruir el saldo que
    /// quedó tras ESTE cobro (el saldo corriente ya pudo cambiar por abonos posteriores).
    /// </summary>
    public static CreditPaymentReceiptContext CreateFrom(
        ReceivablePayment payment,
        decimal currentDebtUsd,
        decimal todayRate,
        IReadOnlyDictionary<int, decimal> laterAllocationsByReceivable,
        MonetaryProfileDto? profile = null)
    {
        ArgumentNullException.ThrowIfNull(payment);

        // Sin tasa del día registrada se conserva la tasa del abono para no romper el comprobante.
        decimal effectiveTodayRate = todayRate > 0m ? todayRate : payment.PaymentRate;

        var firstSale = payment.Allocations
            .OrderBy(allocation => allocation.Id)
            .Select(allocation => allocation.Receivable?.Sale)
            .FirstOrDefault(sale => sale is not null);

        string mode = ReceiptCurrencyFormatter.NormalizeMode(
            firstSale?.MonetaryMode,
            firstSale is null ? profile : null);
        // Un abono sin venta cargada (dato legacy) usa el perfil; una venta legacy con snapshot
        // vacío conserva el default histórico USD/VES (jamás reinterpreta historia).
        bool useProfileFallback = firstSale is null;
        var primary = ReceiptCurrencyFormatter.ResolveCurrency(
            firstSale?.PrimaryCurrencyCode,
            useProfileFallback && !string.IsNullOrWhiteSpace(profile?.PrimaryCurrencyCode)
                ? profile!.PrimaryCurrencyCode
                : ReceiptCurrencyFormatter.UsdCode,
            profile);
        string secondaryFallback = useProfileFallback
            && profile?.Mode == Core.DTOs.MonetaryMode.Dual
            && !string.IsNullOrWhiteSpace(profile.SecondaryCurrencyCode)
                ? profile.SecondaryCurrencyCode!
                : ReceiptCurrencyFormatter.VesCode;
        var secondary = string.Equals(mode, ReceiptCurrencyFormatter.SingleMode, StringComparison.Ordinal)
            ? null
            : ReceiptCurrencyFormatter.ResolveCurrency(
                firstSale?.SecondaryCurrencyCode,
                secondaryFallback,
                profile);

        var lines = payment.Allocations
            .OrderBy(allocation => allocation.Id)
            .Select(allocation =>
            {
                decimal laterUsd = laterAllocationsByReceivable.TryGetValue(allocation.ReceivableId, out decimal later)
                    ? later
                    : 0m;
                decimal balanceAfterUsd = Math.Round(
                    (allocation.Receivable?.BalanceUSD ?? 0m) + laterUsd, 4, MidpointRounding.AwayFromZero);
                decimal balanceBeforeUsd = Math.Round(
                    balanceAfterUsd + allocation.AmountUSD, 4, MidpointRounding.AwayFromZero);

                return new CreditPaymentReceiptLine(
                    allocation.ReceivableId,
                    allocation.Receivable?.SaleId ?? 0,
                    allocation.Receivable?.Sale?.InvoiceNumber,
                    balanceBeforeUsd,
                    allocation.AmountUSD,
                    allocation.AmountBsS,
                    allocation.FxDifferenceBsS,
                    balanceAfterUsd);
            })
            .ToList();

        return new CreditPaymentReceiptContext
        {
            PrimaryCurrencyCode = primary.Code,
            PrimaryCurrencySymbol = primary.Symbol,
            SecondaryCurrencyCode = secondary?.Code,
            SecondaryCurrencySymbol = secondary?.Symbol,
            MonetaryMode = mode,
            PaymentId = payment.Id,
            CustomerId = payment.CustomerId,
            CustomerName = payment.Customer?.Name ?? "Consumidor Final",
            CustomerCedula = payment.Customer?.CedulaOrRif,
            ReceivedAtUtc = payment.ReceivedAtUtc,
            PaymentRate = payment.PaymentRate,
            TodayRate = effectiveTodayRate,
            PaymentMethodName = payment.PaymentMethod?.Name,
            ReferenceNumber = payment.ReferenceNumber,
            TotalAmountUSD = payment.AmountUSD,
            TotalAmountBsS = payment.AmountBsS,
            FxDifferenceBsS = payment.FxDifferenceBsS,
            CurrentDebtUSD = Math.Round(currentDebtUsd, 4, MidpointRounding.AwayFromZero),
            CurrentDebtTodayBsS = PricingCalculator.ToBsS(currentDebtUsd, effectiveTodayRate),
            Lines = lines
        };
    }
}
