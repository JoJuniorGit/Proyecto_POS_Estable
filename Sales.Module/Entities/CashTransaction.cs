using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

public enum CashTransactionType
{
    Income,  // Added to Drawer (e.g., cash sale)
    Expense  // Removed from Drawer (e.g., cash advance)
}

public enum CashTransactionSource
{
    // Mantener compatibilidad con valores ya almacenados en DB.
    Opening = 0,
    SalePayment = 1,
    CashAdvance = 2,
    ManualAdjustment = 3,
    Closing = 4,

    // Nuevos valores para auditar el origen exacto de cash-in / cash-out.
    CashIn = 5,
    CashOut = 6,

    // Paso 11/12 (S5): abonos CxC y devoluciones de credito; se anexan al final
    // para no renumerar los valores ya persistidos.
    CreditPayment = 7,
    CreditRefund = 8,

    // Paso 13 / 8.188-T1 (S6): egreso de caja chica y su reversa compensatoria.
    PettyCashExpense = 9,
    PettyCashReversal = 10
}

public class CashTransaction
{
    public int Id { get; set; }

    public int SessionId { get; set; }
    public CashDrawerSession Session { get; set; } = null!;

    public DateTime TransactionTime { get; set; } = DateTime.UtcNow;
    [NotMapped] public DateTime TransactionTimeLocal { get; set; }

    public CashTransactionType Type { get; set; }
    public CashTransactionSource Source { get; set; }

    // Amount stored in USD, but UI typically shows Bs.S (AmountUsd * ExchangeRate)
    [Column(TypeName = "decimal(18,4)")]
    public decimal AmountUsd { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal ExchangeRate { get; set; }

    // Amount stored in local currency (Bs.S) so the cash register can be computed without
    // USD -> Bs.S recalculation (avoids rounding drift).
    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountLocal { get; set; }

    public string Description { get; set; } = string.Empty;

    // e.g., SaleId. Nullable for things like Opening/ManualAdjustment
    public int? ReferenceId { get; set; }

    // Explicit foreign key mapping properly to actual Sale Id
    public int? SaleId { get; set; }
    public Sale? Sale { get; set; }

    public bool IsPhysicalCash { get; set; } = true;

    public int? PaymentMethodId { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }

    // 8.188-T1 (Paso 13, S6): vinculo del movimiento con el gasto de caja chica. Tanto el
    // Expense original (PettyCashExpense) como su reversa compensatoria (PettyCashReversal)
    // apuntan al mismo gasto. La FK vive aqui para evitar el ciclo con PettyCashExpenses.
    public int? PettyCashExpenseId { get; set; }
    public PettyCashExpense? PettyCashExpense { get; set; }
}
