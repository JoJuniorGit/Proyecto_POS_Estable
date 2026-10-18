using Core.Entities;

namespace Core.DTOs;

/// <summary>
/// Paso 11 (S1): perfil crediticio observable del cliente. El Crédito Disponible y el
/// Porcentaje de Utilización se calculan en tiempo real (no se persisten): Disponible =
/// Límite autorizado − Saldo deudor actual de obligaciones abiertas.
/// </summary>
public class CreditProfileDto
{
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsCreditEnabled { get; set; }

    /// <summary>Estado de bloqueo vigente (Active / SuspendedByMora / ManuallyBlocked).</summary>
    public CreditStatus CreditStatus { get; set; }

    public string? CreditNotes { get; set; }

    /// <summary>Cupo máximo de deuda simultánea autorizada (USD).</summary>
    public decimal CreditLimitUSD { get; set; }

    /// <summary>Suma de BalanceUSD de obligaciones abiertas con saldo &gt; 0.005 (USD 4dp).</summary>
    public decimal CurrentDebtUSD { get; set; }

    /// <summary>Límite − saldo deudor; puede ser negativo para reportería (elegibilidad lo bloquea).</summary>
    public decimal AvailableCreditUSD { get; set; }

    /// <summary>Saldo deudor / límite × 100 (2dp). Sin límite positivo se reporta 0 para no dividir por cero.</summary>
    public decimal UtilizationPercent { get; set; }

    /// <summary>Plazo estándar de pago en días continuos desde la emisión.</summary>
    public int PaymentTermDays { get; set; }

    /// <summary>Días de gracia posteriores al vencimiento antes de considerar mora.</summary>
    public int CreditGraceDays { get; set; }

    /// <summary>Suma de saldos vencidos fuera del período de gracia (USD 4dp).</summary>
    public decimal OverdueUSD { get; set; }

    /// <summary>Cantidad de obligaciones abiertas vencidas fuera del período de gracia.</summary>
    public int OverdueCount { get; set; }
}

/// <summary>
/// Resultado estructurado de la evaluación de solvencia en tiempo real (S2/S4): elegibilidad
/// del monto a financiar con los bloqueos aplicables en español y el cupo disponible calculado.
/// F2a NO admite sobregiro: exceder el disponible bloquea (T3 agrega la autorización supervisor).
/// </summary>
public class CreditEligibilityResultDto
{
    public bool IsEligible { get; set; }

    /// <summary>Motivos de bloqueo en español (vacío cuando IsEligible es true).</summary>
    public System.Collections.Generic.List<string> Blockers { get; set; } = new();

    /// <summary>T3b1 (S4): nombre del cliente evaluado, para el contexto de presentación de la autorización.</summary>
    public string? CustomerName { get; set; }

    public decimal RequestedAmountUSD { get; set; }
    public decimal CreditLimitUSD { get; set; }
    public decimal CurrentDebtUSD { get; set; }
    public decimal AvailableCreditUSD { get; set; }
}

/// <summary>
/// T3b1 (F2b, S4): señal de dominio del cierre a crédito cuando el ÚNICO bloqueo de solvencia es
/// el sobregiro de cupo (monto a financiar &gt; crédito disponible). El controlador la traduce al
/// contrato 403 con authorizationAction=CreditOverLimit y el contexto exacto que el hub debe
/// reproducir para solicitar la autorización; con un token vigente el cierre reintenta habilitando
/// SOLO esta excepción. Los demás bloqueos (bloqueo manual, mora, deshabilitado, plazo inválido,
/// cliente inactivo/inexistente) jamás producen esta señal ni se omiten con un token.
/// Vive junto a los contratos de crédito (Core) porque cruza la frontera dominio → API.
/// </summary>
public sealed class CreditOverLimitRequiresAuthorizationException : InvalidOperationException
{
    public int SaleId { get; }

    public int CustomerId { get; }

    public string? CustomerName { get; }

    /// <summary>Monto efectivo en USD que se pretende financiar (derivado del Bs.S efectivo / tasa de emisión).</summary>
    public decimal FinancedUSD { get; }

    /// <summary>Crédito disponible del cliente al momento del intento (puede ser negativo).</summary>
    public decimal AvailableCreditUSD { get; }

    public CreditOverLimitRequiresAuthorizationException(
        int saleId,
        int customerId,
        string? customerName,
        decimal financedUSD,
        decimal availableCreditUSD,
        string message)
        : base(message)
    {
        SaleId = saleId;
        CustomerId = customerId;
        CustomerName = customerName;
        FinancedUSD = financedUSD;
        AvailableCreditUSD = availableCreditUSD;
    }
}

/// <summary>
/// T3a (F2b, Paso 11/12, S3/S6): estado de cuenta dual del cliente. Reúne el perfil crediticio,
/// la cartera completa (abiertas y cerradas) con el equivalente Bs.S del saldo a la tasa del día
/// y el historial de abonos con el diferencial cambiario realizado. La deuda permanece en USD:
/// el Bs.S es siempre una proyección a la tasa vigente y nunca revalúa el principal.
/// </summary>
public class CreditStatementDto
{
    public CreditProfileDto Profile { get; set; } = new();

    /// <summary>Tasa efectiva del día (BCV vigente); 0 cuando no hay tasa registrada.</summary>
    public decimal TodayRate { get; set; }

    /// <summary>Equivalente en Bs.S del saldo deudor vigente a <see cref="TodayRate"/> (2dp).</summary>
    public decimal CurrentDebtTodayBsS { get; set; }

    public System.Collections.Generic.List<CreditStatementReceivableDto> Receivables { get; set; } = new();
    public System.Collections.Generic.List<CreditStatementPaymentDto> Payments { get; set; } = new();
}

/// <summary>Obligación CxC del estado de cuenta: snapshot de emisión + saldo vigente proyectado al día.</summary>
public class CreditStatementReceivableDto
{
    public int ReceivableId { get; set; }
    public int SaleId { get; set; }
    public int? InvoiceNumber { get; set; }

    public decimal OriginalUSD { get; set; }
    public decimal BalanceUSD { get; set; }

    /// <summary>Contravalor financiado a la emisión (snapshot inmutable).</summary>
    public decimal OriginalBsS { get; set; }

    /// <summary>Tasa BCV de emisión congelada.</summary>
    public decimal HistoricalRate { get; set; }

    /// <summary>Equivalente Bs.S del saldo vigente a la tasa del día (proyección, no revaluación).</summary>
    public decimal BalanceTodayBsS { get; set; }

    public System.DateTime IssuedAtUtc { get; set; }
    public System.DateTime DueDateUtc { get; set; }

    /// <summary>Nombre del estado (Vigente / Parcial / Vencida / Pagada / Anulada).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>true cuando la obligación está abierta y superó el vencimiento más los días de gracia (mora).</summary>
    public bool IsOverdue { get; set; }
}

/// <summary>Abono del historial del estado de cuenta: totales, tasa congelada y FX realizado.</summary>
public class CreditStatementPaymentDto
{
    public int PaymentId { get; set; }
    public decimal AmountUSD { get; set; }
    public decimal AmountBsS { get; set; }
    public decimal PaymentRate { get; set; }
    public decimal FxDifferenceBsS { get; set; }
    public System.DateTime ReceivedAtUtc { get; set; }
    public int? PaymentMethodId { get; set; }
    public string? PaymentMethodName { get; set; }
    public string? ReferenceNumber { get; set; }
    public int? ReceivedByUserId { get; set; }
    public int? CashTransactionId { get; set; }
}
