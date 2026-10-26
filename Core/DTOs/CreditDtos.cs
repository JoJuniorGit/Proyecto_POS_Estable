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

    /// <summary>Límite − saldo deudor acotado a cero (nunca negativo); la utilización puede superar 100%.</summary>
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
/// T4-prep (S1/S4): escritura administrativa del perfil crediticio del cliente. El estado
/// <see cref="CreditStatus.SuspendedByMora"/> es administrado por el sistema (job de mora) y se
/// rechaza si la UI intenta asignarlo manualmente; solo <see cref="CreditStatus.Active"/> y
/// <see cref="CreditStatus.ManuallyBlocked"/> son estados manuales válidos.
/// </summary>
public class CustomerCreditProfileUpdateDto
{
    public bool IsCreditEnabled { get; set; }

    /// <summary>Cupo máximo de deuda simultánea autorizada (USD, no negativo).</summary>
    public decimal CreditLimitUSD { get; set; }

    /// <summary>Plazo estándar de pago en días continuos (1..365).</summary>
    public int PaymentTermDays { get; set; } = 30;

    /// <summary>Días de gracia posteriores al vencimiento (no negativos).</summary>
    public int CreditGraceDays { get; set; }

    /// <summary>Notas operativas, garantías o acuerdos especiales (máximo 1000 caracteres).</summary>
    public string? CreditNotes { get; set; }

    /// <summary>Estado manual: Active o ManuallyBlocked. SuspendedByMora se rechaza.</summary>
    public CreditStatus CreditStatus { get; set; } = CreditStatus.Active;
}

/// <summary>
/// T4-prep (S3): filtros de la cartera global de cuentas por cobrar. <see cref="Status"/> acepta
/// los nombres del enum (<c>Vigente</c>, <c>Parcial</c>, <c>Vencida</c>, <c>Pagada</c>,
/// <c>Anulada</c>) sin distinguir mayúsculas; <see cref="Search"/> busca por cliente, cédula/RIF
/// o número de factura. <see cref="OverdueOnly"/> usa la mora dinámica (vencimiento + gracia del
/// cliente), no el estado persistido.
/// </summary>
public class CreditReceivablesFilterDto
{
    public int? CustomerId { get; set; }

    public string? Status { get; set; }

    public bool OverdueOnly { get; set; }

    public string? Search { get; set; }

    /// <summary>
    /// 8.202-H4 (S7): página opcional (1-based). Sin <see cref="PageSize"/> se conserva el
    /// contrato histórico que devuelve toda la cartera filtrada.
    /// </summary>
    public int? Page { get; set; }

    /// <summary>
    /// 8.202-H4 (S7): tamaño de página opcional, acotado a 1..500. Ausente = sin paginación.
    /// </summary>
    public int? PageSize { get; set; }
}

/// <summary>
/// T4-prep (S3/S6): cartera global con proyección Bs.S a la tasa del día. Los totales cubren la
/// cartera abierta del conjunto filtrado (Vigente/Parcial/Vencida con saldo &gt; 0.005 USD), sin
/// sumar obligaciones pagadas, anuladas ni polvo decimal.
/// </summary>
public class CreditReceivablesListDto
{
    public System.Collections.Generic.List<CreditReceivableListItemDto> Items { get; set; } = new();

    /// <summary>Suma de saldos abiertos del conjunto filtrado (USD 4dp).</summary>
    public decimal TotalOpenUSD { get; set; }

    /// <summary>Suma de saldos abiertos en mora dinámica del conjunto filtrado (USD 4dp).</summary>
    public decimal TotalOverdueUSD { get; set; }

    /// <summary>Cantidad de obligaciones abiertas del conjunto filtrado.</summary>
    public int OpenCount { get; set; }

    /// <summary>Tasa efectiva del día usada para la proyección Bs.S; 0 cuando no hay tasa.</summary>
    public decimal TodayRate { get; set; }

    /// <summary>8.202-H4 (S7): cantidad total de obligaciones del conjunto filtrado (antes de paginar).</summary>
    public int TotalCount { get; set; }

    /// <summary>8.202-H4 (S7): página aplicada; null cuando el cliente no pidió paginación.</summary>
    public int? Page { get; set; }

    /// <summary>8.202-H4 (S7): tamaño de página aplicado; null cuando no se pidió paginación.</summary>
    public int? PageSize { get; set; }

    /// <summary>8.202-H4 (S7): true cuando quedan más filas después de la página devuelta.</summary>
    public bool HasMore { get; set; }
}

/// <summary>Obligación CxC de la cartera global con su snapshot de emisión y saldo proyectado al día.</summary>
public class CreditReceivableListItemDto
{
    public int ReceivableId { get; set; }
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerCedulaOrRif { get; set; } = string.Empty;

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

    /// <summary>true cuando está abierta y superó vencimiento + días de gracia del cliente.</summary>
    public bool IsOverdue { get; set; }
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

    /// <summary>Cupo disponible para nuevas compras financiadas, acotado a cero (nunca negativo).</summary>
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

    /// <summary>Crédito disponible del cliente al momento del intento, acotado a cero (nunca negativo).</summary>
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

/// <summary>
/// H1 (T6, S3/S4): resultado del barrido de cartera de mora. Solo cuenta cambios efectivos de
/// la corrida: una segunda ejecución idempotente devuelve los contadores de cambios en cero y
/// la cantidad de clientes con crédito habilitado reevaluada.
/// </summary>
public class CreditMoraEvaluationResultDto
{
    /// <summary>Obligaciones abiertas materializadas como Vencida en esta corrida.</summary>
    public int OverdueMaterializedCount { get; set; }

    /// <summary>Clientes Active suspendidos por mora en esta corrida.</summary>
    public int SuspendedCount { get; set; }

    /// <summary>Clientes SuspendedByMora rehabilitados a Active en esta corrida.</summary>
    public int ReactivatedCount { get; set; }

    /// <summary>Clientes con IsCreditEnabled evaluados en esta corrida.</summary>
    public int EvaluatedCustomersCount { get; set; }
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
