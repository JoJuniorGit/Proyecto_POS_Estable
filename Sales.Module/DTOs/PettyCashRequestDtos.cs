using System;
using Sales.Module.Entities;

namespace Sales.Module.DTOs;

/// <summary>
/// T2a (Paso 13 / 8.188, S1): contrato del registro de un egreso de caja chica. El monto es el
/// nominal de la divisa física que sale de la gaveta; el contravalor bimonetario y la tasa BCV
/// los resuelve el servidor (nunca llegan del cliente). La clave de idempotencia es opcional y
/// un reintento con la misma clave replica el gasto sin doble movimiento.
/// </summary>
public class PettyCashExpenseRequest
{
    /// <summary>Divisa física extraída: Bs.S (gaveta local) o USD (gaveta dura).</summary>
    public PettyCashCurrency Currency { get; set; }

    /// <summary>Monto nominal de la divisa física (mayor a cero).</summary>
    public decimal Amount { get; set; }

    public PettyCashCategory Category { get; set; }

    /// <summary>Concepto detallado del gasto (obligatorio, máximo 500 caracteres).</summary>
    public string? Concept { get; set; }

    /// <summary>Beneficiario o proveedor (obligatorio, máximo 150 caracteres).</summary>
    public string? Beneficiary { get; set; }

    /// <summary>Número de referencia del comprobante, recibo o factura (obligatorio, máximo 100).</summary>
    public string? ReferenceNumber { get; set; }

    /// <summary>Clave de idempotencia del registro (opcional, máximo 100 caracteres).</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>T2a (S5): solicitud de anulación lógica del gasto con motivo obligatorio.</summary>
public class PettyCashCancelRequest
{
    /// <summary>Motivo de la anulación (obligatorio, máximo 500 caracteres).</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// T2a (Paso 13 / 8.188, S1/S4/S5): vista del gasto de caja chica para caja, supervisión y UI.
/// Expone los snapshots bimonetarios congelados, el correlativo del vale (VCC-000001), el estado
/// de anulación y el movimiento de caja vinculado (egreso original o reversa compensatoria).
/// </summary>
public class PettyCashExpenseResultDto
{
    public int Id { get; set; }

    public long VoucherNumber { get; set; }

    /// <summary>Formato de presentación del vale: VCC-000001.</summary>
    public string VoucherCode => $"VCC-{VoucherNumber:D6}";

    public int SessionId { get; set; }

    public PettyCashCategory Category { get; set; }

    public PettyCashCurrency Currency { get; set; }

    /// <summary>Contravalor Bs.S congelado (2dp).</summary>
    public decimal AmountBsS { get; set; }

    /// <summary>Contravalor USD congelado (4dp).</summary>
    public decimal AmountUsd { get; set; }

    /// <summary>Tasa BCV aplicada en el registro (snapshot inmutable).</summary>
    public decimal HistoricalRate { get; set; }

    public string Concept { get; set; } = string.Empty;

    public string Beneficiary { get; set; } = string.Empty;

    public string ReferenceNumber { get; set; } = string.Empty;

    public int CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Supervisor que autorizó el gasto (o el propio usuario cuando no se requirió).</summary>
    public int? AuthorizedByUserId { get; set; }

    /// <summary>Solicitud de autorización consumida (solo cuando aplicó umbral).</summary>
    public int? AuthorizationRequestId { get; set; }

    public PettyCashStatus Status { get; set; }

    public DateTime? CancelledAtUtc { get; set; }

    public int? CancelledByUserId { get; set; }

    public string? CancellationReason { get; set; }

    /// <summary>Movimiento de caja vinculado (egreso original o reversa compensatoria).</summary>
    public int? CashTransactionId { get; set; }

    /// <summary>true cuando el registro se sirvió desde la clave de idempotencia (replay).</summary>
    public bool IsReplay { get; set; }
}
