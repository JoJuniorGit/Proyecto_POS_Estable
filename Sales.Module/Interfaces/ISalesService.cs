using Core.DTOs;
using Sales.Module.Entities;
using Sales.Module.DTOs;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

public record PaymentInfo(int PaymentMethodId, decimal Amount, decimal AmountLocal, string? Reference);

public interface ISalesService
{
    Task<SaleDto> StartSaleAsync(int? cashierId = null);
    Task<SaleDto> GetSaleAsync(int saleId);
    Task<SaleDto> AddItemAsync(int saleId, int productId, decimal quantity, decimal exchangeRate, decimal? customUnitPriceUsd = null, decimal? customUnitPriceLocal = null, bool isPriceOverrideAuthorized = false, int? actingUserId = null);
    Task<SaleDto> RemoveItemAsync(int saleId, int itemId, decimal exchangeRate, int? actingUserId = null);
    Task<SaleDto> UpdateItemQuantityAsync(int saleId, int itemId, decimal quantity, decimal exchangeRate, int? actingUserId = null);
    Task<SaleDto> UpdateExchangeRateAsync(int saleId, decimal exchangeRate, int? actingUserId = null);
    Task<SaleDto> UpdatePriceListAsync(int saleId, string priceListType, int? actingUserId = null);
    Task CancelSaleAsync(int saleId, int? actingUserId = null);
    Task<int> CompleteSaleAsync(int saleId, decimal exchangeRate, IEnumerable<PaymentInfo> payments, decimal roundingAdjustment = 0, int? cashierId = null, bool isPendingPickup = false, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null, System.Threading.CancellationToken cancellationToken = default, int? actingUserId = null);
    Task<SaleHistoryDto> ConfirmPickupAsync(int saleId, int? actingUserId = null);
    Task<IEnumerable<PendingPickupDto>> GetPendingPickupsAsync(int? cashierId = null, int limit = 200, int offset = 0);
    Task<(IEnumerable<SaleHistoryDto> Items, int TotalCount)> GetSalesHistoryAsync(int page, int pageSize, System.DateTime? startDate, System.DateTime? endDate, string? search = null, int? cashierId = null);
    Task<SaleHistoryDto> GetSaleHistoryDetailAsync(int saleId);

    // OnHold / Customer Methods
    Task<SaleDto> HoldSaleAsync(int saleId, HoldSaleRequestDto request, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null, int? actingUserId = null);
    Task<SaleDto> UpdateSaleItemsAsync(int saleId, UpdateSaleItemsRequestDto request, bool isPriceOverrideAuthorized = false, int? actingUserId = null);
    Task<SaleDto> AddPaymentToHoldSaleAsync(int saleId, AddPaymentRequestDto request, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null, int? actingUserId = null);

    /// <summary>8.29-A05: aplica varios abonos a una venta en espera en UNA sola transacción
    /// (todo o nada): si CUALQUIER abono del lote falla la validación, ninguno se persiste.</summary>
    Task<SaleDto> AddPaymentsBatchToHoldSaleAsync(int saleId, List<AddPaymentRequestDto> payments, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null, int? actingUserId = null);
    Task<IEnumerable<SaleDto>> GetPendingSalesAsync(int? cashierId = null, int limit = 200, int offset = 0);

    /// <summary>8.14-N1: total de ventas OnHold para paginación de UI.</summary>
    Task<int> CountPendingSalesAsync(int? cashierId = null);

    /// <summary>8.14-N1: total de retiros pendientes para paginación de UI.</summary>
    Task<int> CountPendingPickupsAsync(int? cashierId = null);
    Task<SaleDto> UpdateSaleCustomerAsync(int saleId, int customerId, int? actingUserId = null);
    Task<(IEnumerable<CustomerDto> Items, int TotalCount)> GetCustomersAsync(string? query = null, int page = 1, int pageSize = 20, bool recentOnly = false);

    Task<CustomerDto> GetDefaultCustomerAsync();
    Task<CustomerDto> CreateCustomerAsync(CreateCustomerDto request);
    Task<CustomerDto> UpdateCustomerAsync(int id, UpdateCustomerDto request);
    Task DeleteCustomerAsync(int id);

    /// <summary>
    /// Recalculates prices and totals for all OnHold sales using the new exchange rate.
    /// Updates both USD prices from the catalog and BsS conversion.
    /// Existing payments (abonos) are NOT modified.
    /// </summary>
    Task<int> RecalculateOnHoldSalesAsync(decimal newExchangeRate);

    Task<SaleDto> ClaimSaleAsync(int saleId, SaleClaimAction action, int? actingUserId, System.Threading.CancellationToken cancellationToken = default);

    Task<SaleDto> ReleaseSaleAsync(int saleId, int? actingUserId, bool force = false, System.Threading.CancellationToken cancellationToken = default);

    Task<Sale> CreateCashAdvanceSaleAsync(
        decimal requestedAmountLocal,
        decimal commissionAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId = null,
        string? userName = null,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? existingTransaction = null);
}
