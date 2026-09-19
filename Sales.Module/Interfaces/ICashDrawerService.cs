using Sales.Module.DTOs;
using Sales.Module.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

public class CashAdvanceResultDto
{
    public CashTransactionResponseDto ExpenseTransaction { get; set; } = null!;
    public CashTransactionResponseDto IncomeTransaction { get; set; } = null!;
    public decimal RequestedAmountLocal { get; set; }
    public decimal CommissionAmountLocal { get; set; }
    public decimal TotalChargedLocal { get; set; }
    public decimal CommissionPercentage { get; set; }
    public int? RelatedSaleId { get; set; }
    public int? InvoiceNumber { get; set; }
}

public interface ICashDrawerService
{
    Task<CashDrawerSessionResponseDto?> GetActiveSessionAsync(CancellationToken cancellationToken = default);
    Task<CashDrawerSessionResponseDto?> GetActiveSessionWithTransactionsAsync(CancellationToken cancellationToken = default);
    Task<CashDrawerSessionResponseDto> GetOrCreateActiveSessionAsync(decimal currentExchangeRate, CancellationToken cancellationToken = default);
    Task<CashDrawerSessionResponseDto> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate, CancellationToken cancellationToken = default);
    Task<CashDrawerSessionResponseDto> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate, CancellationToken cancellationToken = default);
    Task RolloverSessionAfterClosureAsync(decimal currentExchangeRate, CancellationToken cancellationToken = default);

    Task<CashTransactionResponseDto> AddTransactionAsync(
        int sessionId,
        CashTransactionType type,
        CashTransactionSource source,
        decimal amountLocal,
        decimal amountUsd,
        decimal exchangeRate,
        string description,
        int? referenceId = null,
        bool isPhysicalCash = true,
        int? paymentMethodId = null,
        CancellationToken cancellationToken = default);

    Task<CashTransactionResponseDto> RecordSaleChangeAsync(
        int sessionId,
        decimal changeUsd,
        decimal changeBsS,
        decimal exchangeRate,
        string description,
        int saleId,
        int? cashPaymentMethodId,
        decimal pendingCashIncomeBsS = 0m,
        CancellationToken cancellationToken = default);

    Task<decimal> GetCurrentBalanceLocalAsync(int sessionId, CancellationToken cancellationToken = default);
    Task<System.Collections.Generic.List<CashTransactionResponseDto>> GetHistoryAsync(int limit = 300, CancellationToken cancellationToken = default);
    Task<Core.DTOs.PagedResultDto<CashTransactionResponseDto>> GetHistoryPagedAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);
}
