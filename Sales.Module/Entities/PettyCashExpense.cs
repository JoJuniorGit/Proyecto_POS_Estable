using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

/// <summary>
/// 8.188-T1 (Paso 13, S1/S6): egreso operativo de caja chica del turno. Registra la moneda
/// fisica que sale de la gaveta y su valoracion bimonetaria con la tasa BCV congelada
/// (snapshot inmutable: al anular jamas se relee la tasa), mas la trazabilidad administrativa
/// (categoria, concepto, beneficiario y referencia del comprobante).
/// La anulacion es logica (Status/CancelledAtUtc/CancelledByUserId/motivo) y compensa con un
/// CashTransaction Income PettyCashReversal: el registro historico nunca se borra. El supervisor
/// que aprobo la anulacion (cuando la jerarquia lo exigio) se vincula en
/// <see cref="CancellationAuthorizedByUserId"/> (H3 del cierre 8.188).
/// El vinculo con el libro transaccional vive en <see cref="CashTransaction.PettyCashExpenseId"/>
/// (el gasto y su reversa apuntan al gasto), evitando el ciclo de FKs entre ambas tablas.
/// </summary>
public class PettyCashExpense
{
    public int Id { get; set; }

    // Correlativo unico del vale provisto por la secuencia dedicada petty_cash_voucher_seq
    // (formato de presentacion VCC-000001). La unicidad la garantiza IX_PettyCashExpenses_VoucherNumber.
    public long VoucherNumber { get; set; }

    // Sesion de caja activa obligatoria: el gasto pertenece al turno (S6).
    public int SessionId { get; set; }
    public CashDrawerSession Session { get; set; } = null!;

    public PettyCashCategory Category { get; set; }

    // Divisa fisica extraida; AmountBsS/AmountUsd guardan el contravalor informativo bimonetario.
    public PettyCashCurrency Currency { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountBsS { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal AmountUsd { get; set; }

    // Tasa BCV aplicada en el instante del registro (snapshot; no se recalcula al anular).
    [Column(TypeName = "decimal(18,4)")]
    public decimal HistoricalRate { get; set; }

    public string Concept { get; set; } = string.Empty;

    public string Beneficiary { get; set; } = string.Empty;

    // Numero de referencia del comprobante/recibo/factura fisica que respalda la entrega.
    public string ReferenceNumber { get; set; } = string.Empty;

    // T2a (S3): clave de idempotencia del registro. Un reintento con la misma clave replica el
    // gasto persistido (sin doble movimiento) y el indice unico filtrado la protege en DB.
    public string? IdempotencyKey { get; set; }

    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Autorizacion jerarquica (S4): usuario aprobador y solicitud consumida (si aplico umbral).
    public int? AuthorizedByUserId { get; set; }
    public int? AuthorizationRequestId { get; set; }

    public PettyCashStatus Status { get; set; } = PettyCashStatus.Active;

    public DateTime? CancelledAtUtc { get; set; }
    public int? CancelledByUserId { get; set; }

    // H3 (cierre 8.188): supervisor que aprobo la anulacion cuando la jerarquia la exigio
    // (Manager/Cashier anulando con token). Null en la anulacion directa de Admin o cuando el
    // aprobador declarado es el propio ejecutor (no hay supervisor distinto que auditar).
    public int? CancellationAuthorizedByUserId { get; set; }

    public string? CancellationReason { get; set; }

    // Movimiento Expense original y su reversa compensatoria Income (efecto neto 0).
    public ICollection<CashTransaction> CashTransactions { get; set; } = new List<CashTransaction>();
}
