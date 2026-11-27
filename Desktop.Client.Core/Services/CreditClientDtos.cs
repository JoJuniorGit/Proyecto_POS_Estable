using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Core.Entities;
using Sales.Module.Entities;

namespace Desktop.Client.Services;

/// <summary>
/// T4 (F3, S1/S4): perfil crediticio expuesto al POS. Es un espejo del contrato del backend con
/// helpers de presentación; el disponible y la utilización son cálculos del servidor y aquí solo
/// se muestran (la UI no recalcula políticas ni cupos).
/// </summary>
public class CreditProfileClientDto
{
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsCreditEnabled { get; set; }

    /// <summary>Estado de bloqueo (Active / SuspendedByMora / ManuallyBlocked), serializado como creditStatus.</summary>
    [JsonPropertyName("creditStatus")]
    public CreditStatus Status { get; set; }

    public string? CreditNotes { get; set; }
    public decimal CreditLimitUSD { get; set; }
    public decimal CurrentDebtUSD { get; set; }
    public decimal AvailableCreditUSD { get; set; }
    public decimal UtilizationPercent { get; set; }
    public int PaymentTermDays { get; set; }
    public int CreditGraceDays { get; set; }
    public decimal OverdueUSD { get; set; }
    public int OverdueCount { get; set; }

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        CreditStatus.Active => "Activo",
        CreditStatus.SuspendedByMora => "Suspendido por Mora",
        CreditStatus.ManuallyBlocked => "Bloqueado Manualmente",
        _ => "Desconocido"
    };

    /// <summary>El cliente puede operar a crédito sin bloqueo de perfil (el cupo lo valida el servidor).</summary>
    [JsonIgnore]
    public bool IsUsable => IsActive
        && IsCreditEnabled
        && Status == CreditStatus.Active
        && OverdueCount == 0;
}

/// <summary>Escritura administrativa del perfil crediticio (solo Active / ManuallyBlocked son manuales).</summary>
public class CreditProfileUpdateClientDto
{
    public bool IsCreditEnabled { get; set; }
    public decimal CreditLimitUSD { get; set; }
    public int PaymentTermDays { get; set; } = 30;
    public int CreditGraceDays { get; set; }
    public string? CreditNotes { get; set; }

    [JsonPropertyName("creditStatus")]
    public CreditStatus Status { get; set; } = CreditStatus.Active;
}

/// <summary>Filtros de la cartera global CxC (cliente, estado, mora dinámica y búsqueda).</summary>
public class CreditReceivablesFilterClientDto
{
    public int? CustomerId { get; set; }
    public string? Status { get; set; }
    public bool OverdueOnly { get; set; }
    public string? Search { get; set; }
}

/// <summary>Cartera global con proyección Bs.S del saldo a la tasa del día y totales abiertos/vencidos.</summary>
public class CreditReceivablesClientDto
{
    public List<CreditReceivableClientItemDto> Items { get; set; } = new();
    public decimal TotalOpenUSD { get; set; }
    public decimal TotalOverdueUSD { get; set; }
    public int OpenCount { get; set; }
    public decimal TodayRate { get; set; }
}

/// <summary>Obligación CxC con snapshot de emisión y equivalente Bs.S del saldo a la tasa del día.</summary>
public class CreditReceivableClientItemDto
{
    public int ReceivableId { get; set; }
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerCedulaOrRif { get; set; } = string.Empty;
    public int SaleId { get; set; }
    public int? InvoiceNumber { get; set; }
    public decimal OriginalUSD { get; set; }
    public decimal BalanceUSD { get; set; }
    public decimal OriginalBsS { get; set; }
    public decimal HistoricalRate { get; set; }
    public decimal BalanceTodayBsS { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public DateTime DueDateUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsOverdue { get; set; }

    [JsonIgnore]
    public bool IsOpen => BalanceUSD > 0.005m
        && (string.Equals(Status, "Vigente", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "Parcial", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "Vencida", StringComparison.OrdinalIgnoreCase));

    /// <summary>La anulación simple solo procede sin abonos: saldo vigente igual al original.</summary>
    [JsonIgnore]
    public bool IsCancelable => IsOpen && BalanceUSD == OriginalUSD;

    /// <summary>Una obligación abierta o pagada conserva la venta para una devolución posterior.</summary>
    [JsonIgnore]
    public bool CanReturn => IsOpen || string.Equals(Status, "Pagada", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string StatusDisplay => IsOverdue || string.Equals(Status, "Vencida", StringComparison.OrdinalIgnoreCase)
        ? "En mora"
        : Status;

    [JsonIgnore]
    public string CustomerInitials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CustomerName))
            {
                return "?";
            }

            var parts = CustomerName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                return parts[0].Length >= 2
                    ? parts[0][..2].ToUpperInvariant()
                    : parts[0].ToUpperInvariant();
            }

            return string.Concat(
                char.ToUpperInvariant(parts[0][0]),
                char.ToUpperInvariant(parts[1][0]));
        }
    }

    [JsonIgnore]
    public decimal PaidAmountUSD => Math.Max(0m, OriginalUSD - BalanceUSD);

    [JsonIgnore]
    public decimal CollectionProgressPercent => OriginalUSD <= 0m
        ? 0m
        : Math.Clamp(PaidAmountUSD / OriginalUSD * 100m, 0m, 100m);

    [JsonIgnore]
    public string InvoiceDisplay => InvoiceNumber.HasValue ? $"F{InvoiceNumber.Value:D5}" : $"V{SaleId}";

    [JsonIgnore]
    public string IssuedDateDisplay => ToVenezuelaDisplay(IssuedAtUtc, "dd/MM/yyyy");

    [JsonIgnore]
    public string DueDateDisplay => ToVenezuelaDisplay(DueDateUtc, "dd/MM/yyyy");

    [JsonIgnore]
    public string BalanceDisplay => $"$ {BalanceUSD:N2}";

    [JsonIgnore]
    public string BalanceTodayDisplay => $"Bs.S {BalanceTodayBsS:N2}";

    private static string ToVenezuelaDisplay(DateTime value, string format)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return Core.Helpers.TimeZoneHelper.ToVenezuelaTime(utc).ToString(format);
    }
}

/// <summary>Estado de cuenta dual: perfil, cartera completa e historial de abonos con FX realizado.</summary>
public class CreditStatementClientDto
{
    public CreditProfileClientDto Profile { get; set; } = new();
    public decimal TodayRate { get; set; }
    public decimal CurrentDebtTodayBsS { get; set; }
    public List<CreditStatementReceivableClientDto> Receivables { get; set; } = new();
    public List<CreditStatementPaymentClientDto> Payments { get; set; } = new();
}

public class CreditStatementReceivableClientDto
{
    public int ReceivableId { get; set; }
    public int SaleId { get; set; }
    public int? InvoiceNumber { get; set; }
    public decimal OriginalUSD { get; set; }
    public decimal BalanceUSD { get; set; }
    public decimal OriginalBsS { get; set; }
    public decimal HistoricalRate { get; set; }
    public decimal BalanceTodayBsS { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public DateTime DueDateUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsOverdue { get; set; }

    [JsonIgnore]
    public string InvoiceDisplay => InvoiceNumber.HasValue ? $"F{InvoiceNumber.Value:D5}" : $"V{SaleId}";
}

public class CreditStatementPaymentClientDto
{
    public int PaymentId { get; set; }
    public decimal AmountUSD { get; set; }
    public decimal AmountBsS { get; set; }
    public decimal PaymentRate { get; set; }
    public decimal FxDifferenceBsS { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public int? PaymentMethodId { get; set; }
    public string? PaymentMethodName { get; set; }
    public string? ReferenceNumber { get; set; }
    public int? ReceivedByUserId { get; set; }
    public int? CashTransactionId { get; set; }

    [JsonIgnore]
    public string ReceivedAtDisplay => Core.Helpers.TimeZoneHelper
        .ToVenezuelaTime(ReceivedAtUtc.Kind == DateTimeKind.Utc
            ? ReceivedAtUtc
            : DateTime.SpecifyKind(ReceivedAtUtc, DateTimeKind.Utc))
        .ToString("dd/MM/yyyy HH:mm");
}

/// <summary>Abono CxC: monto en USD; la tasa y el contravalor Bs.S los resuelve el servidor.</summary>
public class CreditPaymentRequestClientDto
{
    public int CustomerId { get; set; }
    public decimal AmountUSD { get; set; }
    public int PaymentMethodId { get; set; }
    public string? ReferenceNumber { get; set; }
    public List<int>? ReceivableIds { get; set; }
    public string? IdempotencyKey { get; set; }
}

public class CreditPaymentAllocationClientDto
{
    public int ReceivableId { get; set; }
    public int SaleId { get; set; }
    public int? InvoiceNumber { get; set; }
    public decimal AmountUSD { get; set; }
    public decimal AmountBsS { get; set; }
    public decimal FxDifferenceBsS { get; set; }
    public decimal BalanceAfterUSD { get; set; }
    public ReceivableStatus Status { get; set; }
}

public class CreditPaymentResultClientDto
{
    public int PaymentId { get; set; }
    public int CustomerId { get; set; }
    public decimal AmountUSD { get; set; }
    public decimal AmountBsS { get; set; }
    public decimal PaymentRate { get; set; }
    public decimal FxDifferenceBsS { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public int? ReceivedByUserId { get; set; }
    public int? CashTransactionId { get; set; }
    public int? PaymentMethodId { get; set; }
    public string? ReferenceNumber { get; set; }
    public bool IsReplay { get; set; }
    public List<CreditPaymentAllocationClientDto> Allocations { get; set; } = new();
}

/// <summary>Anulación de una venta a crédito SIN abonos (restituye stock y cupo, sin caja).</summary>
public class CreditCancelClientResultDto
{
    public int SaleId { get; set; }
    public int ReceivableId { get; set; }
    public int CustomerId { get; set; }
    public decimal CancelledOriginalUSD { get; set; }
    public ReceivableStatus Status { get; set; }
    public SaleStatus SaleStatus { get; set; }
    public DateTime CancelledAtUtc { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Devolución total o parcial: el valor por línea lo calcula el servidor sobre la base efectiva.</summary>
public class CreditReturnRequestClientDto
{
    public List<CreditReturnItemClientRequestDto> Items { get; set; } = new();
    public string? Reason { get; set; }
    public string? IdempotencyKey { get; set; }
}

public class CreditReturnItemClientRequestDto
{
    public int SaleItemId { get; set; }
    public decimal Quantity { get; set; }
}

public class CreditReturnItemClientResultDto
{
    public int SaleItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal AmountUSD { get; set; }
}

public class CreditReturnClientResultDto
{
    public int ReturnId { get; set; }
    public int SaleId { get; set; }
    public int ReceivableId { get; set; }
    public int CustomerId { get; set; }
    public decimal ReturnedUSD { get; set; }
    public decimal RefundedUSD { get; set; }
    public decimal RefundedBsS { get; set; }
    public decimal? RefundRate { get; set; }
    public int? CashTransactionId { get; set; }
    public decimal BalanceAfterUSD { get; set; }
    public ReceivableStatus Status { get; set; }
    public SaleStatus SaleStatus { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? Reason { get; set; }
    public bool IsReplay { get; set; }
    public List<CreditReturnItemClientResultDto> Items { get; set; } = new();
}

/// <summary>
/// T4 (F3, S4): 403 de sobregiro de cupo. Conserva el contexto exacto (cliente, venta, financiado
/// y disponible) que el hub debe reenviar para que la solicitud quede ligada al hash del token.
/// </summary>
public sealed class CreditOverLimitAuthorizationException : Exception
{
    public CreditOverLimitAuthorizationException(string? message, CreditOverLimitAuthorizationRequestContext context)
        : base(string.IsNullOrWhiteSpace(message) ? "Se requiere autorización de sobregiro de crédito." : message)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public CreditOverLimitAuthorizationRequestContext Context { get; }

    /// <summary>Discriminador del contrato 403, equivalente a AuthorizationRequiredException.AuthorizationAction.</summary>
    public string AuthorizationAction => nameof(Core.Entities.AuthorizationActionType.CreditOverLimit);
}

// =====================================================================================
// CxC - Deudas Pagadas (historial realizado)
// =====================================================================================

public class PaidReceivablesFilterClientDto
{
    public string? Search { get; set; }
    public DateTime? PaidFrom { get; set; }
    public DateTime? PaidTo { get; set; }
    public int? PaymentMethodId { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

public class PaidReceivablesSummaryClientDto
{
    public int PaidDebtsCount { get; set; }
    public decimal TotalCollectedUsd { get; set; }
    public decimal TotalCollectedLocalHistorical { get; set; }
    public int TotalPaymentsCount { get; set; }
    public decimal AverageDaysToSettle { get; set; }

    [JsonIgnore]
    public string PaidDebtsCountDisplay => PaidDebtsCount.ToString("N0");

    [JsonIgnore]
    public string TotalCollectedUsdDisplay => CurrencyDisplay.FormatPrimary(TotalCollectedUsd);

    [JsonIgnore]
    public string TotalCollectedLocalDisplay => CurrencyDisplay.FormatSecondary(TotalCollectedLocalHistorical);

    [JsonIgnore]
    public string TotalPaymentsCountDisplay => TotalPaymentsCount.ToString("N0");

    [JsonIgnore]
    public string AverageDaysDisplay => $"{CurrencyDisplay.Number(AverageDaysToSettle, 1)} {(AverageDaysToSettle == 1m ? "día" : "días")}";
}

public class PaidReceivablesClientDto
{
    public List<PaidReceivableClientItemDto> Items { get; set; } = new();
    public PaidReceivablesSummaryClientDto Summary { get; set; } = new();
    public int TotalCount { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
    public bool HasMore { get; set; }
}

public class PaidReceivableClientItemDto
{
    public int ObligationId { get; set; }
    public int? InvoiceNumber { get; set; }
    public int SaleId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerDocument { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime PaidAt { get; set; }
    public int DaysToSettle { get; set; }
    public bool SettledOnTime { get; set; }
    public decimal OriginalAmountUsd { get; set; }
    public decimal TotalPaidUsd { get; set; }
    public decimal TotalPaidLocalHistorical { get; set; }
    public int PaymentsCount { get; set; }
    public string PaymentMethodsSummary { get; set; } = string.Empty;

    [JsonIgnore]
    public string InvoiceDisplay => InvoiceNumber.HasValue ? $"F{InvoiceNumber.Value:D5}" : $"V{SaleId}";

    [JsonIgnore]
    public string CustomerInitials
    {
        get
        {
            var parts = CustomerName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            return parts.Length == 1
                ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant()
                : string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]));
        }
    }

    [JsonIgnore]
    public string PaidAtDisplay => ToLocal(PaidAt).ToString("dd/MM/yyyy");

    [JsonIgnore]
    public string IssuedAtDisplay => ToLocal(IssuedAt).ToString("dd/MM/yyyy");

    [JsonIgnore]
    public string PaymentsLabel => PaymentsCount == 1 ? "1 abono" : $"{PaymentsCount} abonos";

    [JsonIgnore]
    public string TotalPaidUsdDisplay => CurrencyDisplay.FormatPrimary(TotalPaidUsd);

    [JsonIgnore]
    public string TotalPaidLocalDisplay => CurrencyDisplay.FormatSecondary(TotalPaidLocalHistorical);

    [JsonIgnore]
    public string SettledOnTimeLabel => SettledOnTime ? "A tiempo" : "Tras mora";

    [JsonIgnore]
    public string StatusDisplay => "Pagada";

    private static DateTime ToLocal(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return Core.Helpers.TimeZoneHelper.ToVenezuelaTime(utc);
    }
}

public class ReceivablePaymentTimelineClientDto
{
    public int ObligationId { get; set; }
    public int? InvoiceNumber { get; set; }
    public int SaleId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerDocument { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal OriginalAmountUsd { get; set; }
    public decimal TotalPaidUsd { get; set; }
    public decimal BalanceUsd { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime DueDate { get; set; }
    public List<ObligationPaymentTimelineItemClientDto> Payments { get; set; } = new();

    [JsonIgnore]
    public string InvoiceDisplay => InvoiceNumber.HasValue ? $"F{InvoiceNumber.Value:D5}" : $"V{SaleId}";

    [JsonIgnore]
    public string OriginalDisplay => CurrencyDisplay.FormatPrimary(OriginalAmountUsd);

    [JsonIgnore]
    public string TotalPaidDisplay => CurrencyDisplay.FormatPrimary(TotalPaidUsd);

    [JsonIgnore]
    public string BalanceDisplay => CurrencyDisplay.FormatPrimary(BalanceUsd);

    [JsonIgnore]
    public bool IsSettled => BalanceUsd <= 0.005m;

    [JsonIgnore]
    public string StatusDisplay => IsSettled ? "Pagada" : Status;
}

public class ObligationPaymentTimelineItemClientDto
{
    public int PaymentId { get; set; }
    public int SequenceNumber { get; set; }
    public DateTime PaidAt { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public string PaymentCurrency { get; set; } = string.Empty;
    public decimal AmountPaidInCurrency { get; set; }
    public decimal ExchangeRateApplied { get; set; }
    public decimal AmountCreditedUsd { get; set; }
    public string? Reference { get; set; }
    public string ImputationMode { get; set; } = string.Empty;
    public string CashierName { get; set; } = string.Empty;
    public decimal RunningBalanceAfterUsd { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Notes { get; set; }

    [JsonIgnore]
    public string SequenceLabel => $"Abono #{SequenceNumber}";

    [JsonIgnore]
    public string PaidAtDisplay => Core.Helpers.TimeZoneHelper.ToVenezuelaTime(
        PaidAt.Kind == DateTimeKind.Utc ? PaidAt : DateTime.SpecifyKind(PaidAt, DateTimeKind.Utc))
        .ToString("dd/MM/yyyy HH:mm");

    [JsonIgnore]
    public bool IsUsd => string.Equals(PaymentCurrency, "USD", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string AmountPaidDisplay => IsUsd
        ? CurrencyDisplay.FormatPrimary(AmountPaidInCurrency)
        : CurrencyDisplay.FormatSecondary(AmountPaidInCurrency);

    [JsonIgnore]
    public string CreditedUsdDisplay => CurrencyDisplay.FormatPrimary(AmountCreditedUsd);

    [JsonIgnore]
    public string ExchangeRateDisplay => CurrencyDisplay.Number(ExchangeRateApplied, 2);

    [JsonIgnore]
    public string RunningBalanceDisplay => CurrencyDisplay.FormatPrimary(RunningBalanceAfterUsd);

    [JsonIgnore]
    public bool IsFinalPayment => RunningBalanceAfterUsd <= 0.005m;
}

public class PaidReceivablePaymentsClientDto
{
    public List<PaidReceivablePaymentItemClientDto> Items { get; set; } = new();
    public PaidReceivablesSummaryClientDto Summary { get; set; } = new();
    public int TotalCount { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
    public bool HasMore { get; set; }
}

public class PaidReceivablePaymentItemClientDto
{
    public int PaymentId { get; set; }
    public DateTime PaidAt { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerDocument { get; set; } = string.Empty;
    public string PaymentMethodName { get; set; } = string.Empty;
    public string PaymentCurrency { get; set; } = string.Empty;
    public decimal AmountPaidInCurrency { get; set; }
    public decimal ExchangeRateApplied { get; set; }
    public decimal AmountCreditedUsd { get; set; }
    public string? Reference { get; set; }
    public string ImputationMode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<PaidPaymentAllocationClientDto> Allocations { get; set; } = new();
    public string InvoiceSummary { get; set; } = string.Empty;

    [JsonIgnore]
    public string PaidAtDisplay => Core.Helpers.TimeZoneHelper.ToVenezuelaTime(
        PaidAt.Kind == DateTimeKind.Utc ? PaidAt : DateTime.SpecifyKind(PaidAt, DateTimeKind.Utc))
        .ToString("dd/MM/yy · HH:mm");

    [JsonIgnore]
    public bool IsUsd => string.Equals(PaymentCurrency, "USD", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string AmountPaidDisplay => IsUsd
        ? CurrencyDisplay.FormatPrimary(AmountPaidInCurrency)
        : CurrencyDisplay.FormatSecondary(AmountPaidInCurrency);

    [JsonIgnore]
    public string CreditedUsdDisplay => CurrencyDisplay.FormatPrimary(AmountCreditedUsd);

    [JsonIgnore]
    public string ExchangeRateDisplay => CurrencyDisplay.Number(ExchangeRateApplied, 2);

    [JsonIgnore]
    public string ReferenceDisplay => string.IsNullOrWhiteSpace(Reference) ? "—" : $"Ref: {Reference}";

    [JsonIgnore]
    public string CustomerInitials
    {
        get
        {
            var parts = CustomerName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            return parts.Length == 1
                ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant()
                : string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]));
        }
    }
}

public class PaidPaymentAllocationClientDto
{
    public int ObligationId { get; set; }
    public int? InvoiceNumber { get; set; }
    public decimal AmountUsd { get; set; }
    public decimal AmountBsS { get; set; }

    [JsonIgnore]
    public string InvoiceDisplay => InvoiceNumber.HasValue ? $"F{InvoiceNumber.Value:D5}" : $"CxC-{ObligationId}";
}
