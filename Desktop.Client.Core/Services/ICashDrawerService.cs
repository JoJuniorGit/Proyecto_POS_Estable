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
    CashOut = 6
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

public interface ICashDrawerService
{
    Task<CashDrawerSessionDto?> GetActiveSessionAsync();
    Task<CashDrawerSessionDto> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate);
    Task<CashDrawerSessionDto> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate);
    Task<decimal> GetCurrentBalanceLocalAsync(int sessionId);
    Task<CashTransactionDto> AddTransactionAsync(int sessionId, decimal amountLocal, CashTransactionType type, CashTransactionSource source, string description, decimal exchangeRate);
    Task<CashAdvanceResultClientDto?> ProcessCashAdvanceAsync(
        int sessionId,
        decimal requestedAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId = null,
        string? userName = null);
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

    public string SourceDisplay => Source switch
    {
        CashTransactionSource.Opening => "Apertura",
        CashTransactionSource.SalePayment => "Venta POS",
        CashTransactionSource.CashAdvance => "Adelanto Efectivo",
        CashTransactionSource.ManualAdjustment => "Ajuste Manual",
        CashTransactionSource.Closing => "Cierre Caja",
        CashTransactionSource.CashIn => "Ingreso de Caja",
        CashTransactionSource.CashOut => "Retiro de Caja",
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
    public decimal OpeningExchangeRate { get; set; }
    public decimal? ClosingBalanceLocal { get; set; }
    public decimal? ClosingExchangeRate { get; set; }

    // Needed by CashDrawerView's "Recent Transactions" grid.
    public System.Collections.Generic.List<CashTransactionDto> Transactions { get; set; } = new();
}
