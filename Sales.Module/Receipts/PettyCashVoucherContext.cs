using System;
using Core.Helpers;
using Sales.Module.Entities;

namespace Sales.Module.Receipts;

/// <summary>
/// T3 (Paso 13 / 8.188, S7): contexto del vale de caja chica (comprobante fisico on-demand).
/// Reune el correlativo VCC-000001, la fecha/hora de Venezuela (turno y vale), la categoria con
/// su etiqueta en espanol, los datos administrativos (concepto/beneficiario/referencia), la
/// divisa fisica con su monto nominal y la valoracion bimonetaria con la tasa BCV congelada,
/// los nombres del cajero creador y del autorizador ("N/A" cuando no aplica), y el sello de
/// anulacion (fecha, usuario y motivo) cuando el gasto fue anulado. Sin persistencia: se arma
/// on-demand desde el gasto y sus usuarios.
/// </summary>
public sealed class PettyCashVoucherContext
{
    /// <summary>Autorizador inexistente en el gasto (no aplico umbral): etiqueta del documento.</summary>
    public const string AuthorizerNotApplicable = "N/A";

    /// <summary>Usuario referenciado pero no encontrado en la tabla (dato anomalo).</summary>
    public const string UserNotFound = "N/D";

    public int ExpenseId { get; init; }

    public long VoucherNumber { get; init; }

    /// <summary>Formato de presentacion del vale: VCC-000001.</summary>
    public string VoucherCode => $"VCC-{VoucherNumber:D6}";

    public int SessionId { get; init; }

    /// <summary>Apertura del turno en UTC (null si la sesion no vino cargada).</summary>
    public DateTime? SessionOpenedAtUtc { get; init; }

    /// <summary>Apertura del turno en hora legal de Venezuela (UTC-4).</summary>
    public DateTime? SessionOpenedAtVenezuela =>
        SessionOpenedAtUtc.HasValue ? TimeZoneHelper.ToVenezuelaTime(SessionOpenedAtUtc.Value) : null;

    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Fecha/hora del egreso en hora legal de Venezuela (UTC-4).</summary>
    public DateTime CreatedAtVenezuela => TimeZoneHelper.ToVenezuelaTime(CreatedAtUtc);

    public PettyCashCategory Category { get; init; }

    public string CategoryLabel => DescribeCategory(Category);

    public string Concept { get; init; } = string.Empty;

    public string Beneficiary { get; init; } = string.Empty;

    public string ReferenceNumber { get; init; } = string.Empty;

    /// <summary>Divisa fisica que salio de la gaveta (Bs.S o USD).</summary>
    public PettyCashCurrency Currency { get; init; }

    /// <summary>Monto nominal de la divisa original (AmountBsS si Bs.S, AmountUsd si USD).</summary>
    public decimal NominalAmount { get; init; }

    /// <summary>Contravalor Bs.S congelado (2dp).</summary>
    public decimal AmountBsS { get; init; }

    /// <summary>Contravalor USD congelado (4dp).</summary>
    public decimal AmountUsd { get; init; }

    /// <summary>Tasa BCV aplicada en el registro (snapshot; jamas se recalcula).</summary>
    public decimal HistoricalRate { get; init; }

    public string CashierName { get; init; } = UserNotFound;

    public string AuthorizerName { get; init; } = AuthorizerNotApplicable;

    public PettyCashStatus Status { get; init; }

    public DateTime? CancelledAtUtc { get; init; }

    /// <summary>Anulacion en hora legal de Venezuela (UTC-4).</summary>
    public DateTime? CancelledAtVenezuela =>
        CancelledAtUtc.HasValue ? TimeZoneHelper.ToVenezuelaTime(CancelledAtUtc.Value) : null;

    public string? CancelledByName { get; init; }

    /// <summary>
    /// H3 (cierre 8.188): supervisor que aprobo la anulacion (nombre resuelto). Null cuando no
    /// hubo autorizador distinto del ejecutor (Admin directo/auto-autorizacion) o cuando el gasto
    /// sigue activo; en esos casos el vale no imprime la linea "Autorizo anulacion". Un ID
    /// presente sin fila de usuario cae a <see cref="UserNotFound"/> (mismo criterio del vale).
    /// </summary>
    public string? CancellationAuthorizedName { get; init; }

    public string? CancellationReason { get; init; }

    public bool IsCancelled => Status == PettyCashStatus.Cancelled;

    /// <summary>
    /// Arma el contexto desde el gasto persistido (con la sesion cargada) y los nombres ya
    /// resueltos de cajero/autorizador/anulador/aprobador de la anulacion. El autorizador sin
    /// usuario registrado en el gasto se muestra como "N/A" (no aplico); un ID presente sin fila
    /// de usuario cae a "N/D".
    /// </summary>
    public static PettyCashVoucherContext CreateFrom(
        PettyCashExpense expense,
        string? cashierName,
        string? authorizerName,
        string? cancellerName = null,
        string? cancellationAuthorizerName = null)
    {
        ArgumentNullException.ThrowIfNull(expense);

        return new PettyCashVoucherContext
        {
            ExpenseId = expense.Id,
            VoucherNumber = expense.VoucherNumber,
            SessionId = expense.SessionId,
            SessionOpenedAtUtc = expense.Session?.OpenedAt,
            CreatedAtUtc = expense.CreatedAtUtc,
            Category = expense.Category,
            Concept = expense.Concept,
            Beneficiary = expense.Beneficiary,
            ReferenceNumber = expense.ReferenceNumber,
            Currency = expense.Currency,
            NominalAmount = expense.Currency == PettyCashCurrency.Usd ? expense.AmountUsd : expense.AmountBsS,
            AmountBsS = expense.AmountBsS,
            AmountUsd = expense.AmountUsd,
            HistoricalRate = expense.HistoricalRate,
            CashierName = string.IsNullOrWhiteSpace(cashierName) ? UserNotFound : cashierName.Trim(),
            AuthorizerName = expense.AuthorizedByUserId is null
                ? AuthorizerNotApplicable
                : string.IsNullOrWhiteSpace(authorizerName) ? UserNotFound : authorizerName.Trim(),
            Status = expense.Status,
            CancelledAtUtc = expense.CancelledAtUtc,
            CancelledByName = string.IsNullOrWhiteSpace(cancellerName) ? null : cancellerName.Trim(),
            CancellationAuthorizedName = expense.CancellationAuthorizedByUserId is null
                ? null
                : string.IsNullOrWhiteSpace(cancellationAuthorizerName) ? UserNotFound : cancellationAuthorizerName.Trim(),
            CancellationReason = expense.CancellationReason
        };
    }

    /// <summary>Etiqueta espanola oficial de la categoria operativa (mandato S1).</summary>
    public static string DescribeCategory(PettyCashCategory category) => category switch
    {
        PettyCashCategory.InsumosLimpieza => "Insumos y Limpieza",
        PettyCashCategory.FletesAcarreo => "Fletes y Acarreo",
        PettyCashCategory.MantenimientoReparaciones => "Mantenimiento y Reparaciones",
        PettyCashCategory.ServiciosPublicos => "Servicios Públicos/Eventuales",
        PettyCashCategory.Papeleria => "Papelería",
        _ => "Varios"
    };
}
