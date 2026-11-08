using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public enum CashDrawerStatus { Open, Closed }
public enum CashTransactionType { Income, Expense }
public enum CashTransactionSource
{
    // Mantener compatibilidad con los valores guardados en la base de datos (Sales.Module).
    Opening = 0,
    SalePayment = 1,
    CashAdvance = 2,
    ManualAdjustment = 3,
    Closing = 4,
    CashIn = 5,
    CashOut = 6,

    // Paso 11/12 (S5): abonos CxC y devoluciones de credito — mismos ids que el servidor.
    CreditPayment = 7,
    CreditRefund = 8,

    // Paso 13 / 8.188-T1 (S6): egreso de caja chica y su reversa compensatoria — mismos ids
    // que el servidor; anexados al final para no renumerar los valores ya persistidos.
    PettyCashExpense = 9,
    PettyCashReversal = 10
}

public class CashAdvanceResultClientDto
{
    public CashTransactionDto ExpenseTransaction { get; set; } = null!;
    public CashTransactionDto IncomeTransaction { get; set; } = null!;
    public decimal RequestedAmountLocal { get; set; }
    public decimal CommissionAmountLocal { get; set; }
    public decimal TotalChargedLocal { get; set; }
    public decimal CommissionPercentage { get; set; }
    public int? RelatedSaleId { get; set; }
    public int? InvoiceNumber { get; set; }
}

public class AdvanceCommissionClientDto
{
    public bool IsTransfer { get; set; }
    public decimal Percentage { get; set; }
}

/// <summary>
/// 8.212-WU4: physical drawer balance for one payment method. <see cref="CurrencySymbol"/> is the
/// profile presentation symbol; <see cref="CanDispense"/> is an operational hint only, the server
/// remains the final authority for the balance.
/// </summary>
public sealed class PhysicalMethodBalanceClientDto
{
    public int PaymentMethodId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = string.Empty;
    public string CurrencySymbol { get; set; } = string.Empty;
    public decimal CurrentBalance { get; set; }
    public bool IsActive { get; set; }
    public bool CanDispense { get; set; }
}

/// <summary>8.212-WU4: per-method opening breakdown sent by the WPF open dialog.</summary>
public sealed class OpenSessionMethodBalanceClientRequest
{
    public int PaymentMethodId { get; set; }
    public decimal OpeningAmount { get; set; }
}

public interface ICashDrawerService
{
    Task<CashDrawerSessionDto?> GetActiveSessionAsync();
    Task<CashDrawerSessionDto> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate, decimal openingBalanceUsd = 0m);

    /// <summary>
    /// 8.212-WU4: optional per-method opening breakdown. The default implementation keeps the
    /// legacy contract for test doubles and callers that do not expose physical methods.
    /// </summary>
    Task<CashDrawerSessionDto> OpenSessionAsync(
        decimal openingBalanceLocal,
        decimal currentExchangeRate,
        decimal openingBalanceUsd,
        System.Collections.Generic.IReadOnlyList<OpenSessionMethodBalanceClientRequest>? methodBalances,
        System.Threading.CancellationToken cancellationToken = default) =>
        OpenSessionAsync(openingBalanceLocal, currentExchangeRate, openingBalanceUsd);

    Task<CashDrawerSessionDto> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate);

    /// <summary>
    /// T4 (Paso 13 / 8.188, S2): cierre con conteo físico USD opcional. Sin valor se conserva el
    /// comportamiento previo; el servidor persiste <c>ClosingBalanceUsd</c> solo cuando se informa.
    /// </summary>
    Task<CashDrawerSessionDto> CloseSessionAsync(
        decimal actualClosingBalanceLocal,
        decimal currentExchangeRate,
        decimal? actualClosingBalanceUsd) => CloseSessionAsync(actualClosingBalanceLocal, currentExchangeRate);

    Task<decimal> GetCurrentBalanceLocalAsync(int sessionId);

    /// <summary>8.212-WU4: live per-method physical balances for the active session.</summary>
    Task<System.Collections.Generic.IReadOnlyList<PhysicalMethodBalanceClientDto>> GetPhysicalMethodBalancesAsync(
        int sessionId,
        System.Threading.CancellationToken cancellationToken = default) =>
        Task.FromResult<System.Collections.Generic.IReadOnlyList<PhysicalMethodBalanceClientDto>>(
            System.Array.Empty<PhysicalMethodBalanceClientDto>());

    Task<CashTransactionDto> AddTransactionAsync(int sessionId, decimal amountLocal, CashTransactionType type, CashTransactionSource source, string description, decimal exchangeRate);

    /// <summary>
    /// T4 (Paso 13 / 8.188, S1/S4/S5): egreso de caja chica. El servidor resuelve la tasa BCV, la
    /// valoración dual y la liquidez; el token de autorización es opcional y solo se envía en el
    /// reintento posterior a un 403 del contrato.
    /// </summary>
    Task<PettyCashExpenseClientDto> RegisterPettyCashExpenseAsync(
        PettyCashExpenseClientRequest request,
        string? authorizationToken = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("El servicio de caja no implementa el registro de caja chica.");

    /// <summary>T4 (S5): anulación lógica con motivo obligatorio y token supervisor opcional.</summary>
    Task<PettyCashExpenseClientDto> CancelPettyCashExpenseAsync(
        int expenseId,
        string reason,
        string? authorizationToken = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("El servicio de caja no implementa la anulación de caja chica.");

    /// <summary>T4 (S1): gastos del turno (activos y anulados) para el panel de caja.</summary>
    Task<System.Collections.Generic.IReadOnlyList<PettyCashExpenseClientDto>> GetPettyCashExpensesAsync(
        int sessionId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("El servicio de caja no implementa el listado de caja chica.");

    /// <summary>T4 (S7): vale de caja chica PDF on-demand; null cuando el gasto no existe.</summary>
    Task<byte[]?> GetPettyCashVoucherAsync(
        int expenseId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("El servicio de caja no implementa el vale de caja chica.");

    /// <summary>T4 (S2): saldos físicos teóricos del turno por moneda (arqueo ciego supervisor).</summary>
    Task<PettyCashPhysicalBalancesClientDto> GetPhysicalBalancesAsync(
        int sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PettyCashPhysicalBalancesClientDto());

    /// <summary>
    /// Historial persistente de movimientos de caja (físicos) de TODAS las sesiones: activa y cerradas,
    /// ordenado por fecha descendente. Permite conservar la trazabilidad tras el cierre de caja.
    /// </summary>
    Task<System.Collections.Generic.List<CashTransactionDto>> GetHistoryAsync(int limit = 300);
    Task<decimal?> GetAdvanceCommissionAsync(bool isTransfer, System.Threading.CancellationToken cancellationToken = default);
    Task<CashAdvanceResultClientDto?> ProcessCashAdvanceAsync(
        int sessionId,
        decimal requestedAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate);

    /// <summary>
    /// 8.212-WU4: method-aware cash advance. The default implementation preserves the legacy
    /// request for test doubles and callers without a physical dispenser selector.
    /// </summary>
    Task<CashAdvanceResultClientDto?> ProcessCashAdvanceAsync(
        int sessionId,
        decimal requestedAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? dispensedCashPaymentMethodId,
        decimal? dispensedAmount,
        int? receivedPaymentMethodId) =>
        ProcessCashAdvanceAsync(
            sessionId,
            requestedAmountLocal,
            paymentMethodId,
            paymentMethodName,
            isTransfer,
            exchangeRate);
}

public class CashTransactionDto
{
    public int Id { get; set; }
    public System.DateTime TransactionTimeLocal { get; set; }
    public string Description { get; set; } = string.Empty;
    public int? InvoiceNumber { get; set; }
    public decimal AmountUsd { get; set; }
    public decimal AmountLocal { get; set; }
    public decimal ExchangeRate { get; set; }
    public CashTransactionType Type { get; set; }
    public CashTransactionSource Source { get; set; }
    public bool IsPhysicalCash { get; set; } = true;
    public int? PaymentMethodId { get; set; }

    public string SourceDisplay => Source switch
    {
        CashTransactionSource.Opening => "Apertura",
        CashTransactionSource.SalePayment => "Venta POS",
        CashTransactionSource.CashAdvance => "Adelanto Efectivo",
        CashTransactionSource.ManualAdjustment => "Ajuste Manual",
        CashTransactionSource.Closing => "Cierre Caja",
        CashTransactionSource.CashIn => "Ingreso de Caja",
        CashTransactionSource.CashOut => "Retiro de Caja",
        CashTransactionSource.CreditPayment => "Abono Crédito",
        CashTransactionSource.CreditRefund => "Devolución Crédito",
        // Paso 13 / 8.188-T1 (S6): mismas etiquetas que el espejo web.
        CashTransactionSource.PettyCashExpense => "Caja Chica",
        CashTransactionSource.PettyCashReversal => "Reversa Caja Chica",
        _ => Source.ToString()
    };

    public string FormattedInvoiceNumber => InvoiceNumber.HasValue 

        ? $"Factura N° {InvoiceNumber.Value}" 
        : (!string.IsNullOrWhiteSpace(Description) ? Description : "-");

    // Bs.S para auditoría / visualización del cash register, sin depender de USD->Bs.S.
    public long AmountBsS => (long)System.Math.Round(AmountLocal, 0, System.MidpointRounding.AwayFromZero);
}

public class CashDrawerSessionDto
{
    public int Id { get; set; }
    public System.DateTime OpenedAt { get; set; }
    public System.DateTime? ClosedAt { get; set; }
    public CashDrawerStatus Status { get; set; }
    public decimal OpeningBalanceLocal { get; set; }
    public decimal OpeningBalanceUsd { get; set; }
    public decimal OpeningExchangeRate { get; set; }
    public decimal? ClosingBalanceLocal { get; set; }
    public decimal? ClosingBalanceUsd { get; set; }
    public decimal? ClosingExchangeRate { get; set; }

    // Needed by CashDrawerView's "Recent Transactions" grid.
    public System.Collections.Generic.List<CashTransactionDto> Transactions { get; set; } = new();

    // 8.212-WU4: null for legacy sessions without WU1 method rows.
    public System.Collections.Generic.List<PhysicalMethodBalanceClientDto>? PhysicalMethodBalances { get; set; }
}
