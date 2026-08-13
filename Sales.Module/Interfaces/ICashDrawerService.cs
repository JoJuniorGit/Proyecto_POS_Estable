using Sales.Module.Entities;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

public class CashAdvanceResultDto
{
    public CashTransaction ExpenseTransaction { get; set; } = null!;
    public CashTransaction IncomeTransaction { get; set; } = null!;
    public decimal RequestedAmountLocal { get; set; }
    public decimal CommissionAmountLocal { get; set; }
    public decimal TotalChargedLocal { get; set; }
    public decimal CommissionPercentage { get; set; }
    public int? RelatedSaleId { get; set; }
    public int? InvoiceNumber { get; set; }
}

public interface ICashDrawerService
{
    Task<CashDrawerSession?> GetActiveSessionAsync();
    Task<CashDrawerSession> GetOrCreateActiveSessionAsync(decimal currentExchangeRate);
    Task<CashDrawerSession> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate);
    Task<CashDrawerSession> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate);
    Task<CashTransaction> AddTransactionAsync(
        int sessionId,
        CashTransactionType type,
        CashTransactionSource source,
        decimal amountLocal,
        decimal amountUsd,
        decimal exchangeRate,
        string description,
        int? referenceId = null,
        bool isPhysicalCash = true);
    Task<decimal> GetCurrentBalanceLocalAsync(int sessionId);
    Task<CashAdvanceResultDto> ProcessCashAdvanceAsync(
        int sessionId,
        decimal requestedAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId = null,
        string? userName = null);
}
