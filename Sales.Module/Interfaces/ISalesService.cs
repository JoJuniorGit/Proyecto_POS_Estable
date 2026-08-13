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
    Task<SaleDto> AddItemAsync(int saleId, int productId, decimal quantity, decimal exchangeRate, decimal? customUnitPriceUsd = null, decimal? customUnitPriceLocal = null);
    Task<SaleDto> RemoveItemAsync(int saleId, int itemId, decimal exchangeRate);
    Task<SaleDto> UpdateItemQuantityAsync(int saleId, int itemId, decimal quantity, decimal exchangeRate);
    Task<SaleDto> UpdateExchangeRateAsync(int saleId, decimal exchangeRate);
    Task<SaleDto> UpdatePriceListAsync(int saleId, string priceListType);
    Task CancelSaleAsync(int saleId);
    Task<int> CompleteSaleAsync(int saleId, decimal exchangeRate, IEnumerable<PaymentInfo> payments, decimal roundingAdjustment = 0, int? cashierId = null, bool isPendingPickup = false);
    Task<SaleHistoryDto> ConfirmPickupAsync(int saleId);
    Task<IEnumerable<PendingPickupDto>> GetPendingPickupsAsync();
    Task<(IEnumerable<SaleHistoryDto> Items, int TotalCount)> GetSalesHistoryAsync(int page, int pageSize, System.DateTime? startDate, System.DateTime? endDate);
    Task<SaleHistoryDto> GetSaleHistoryDetailAsync(int saleId);

    // OnHold / Customer Methods
    Task<SaleDto> HoldSaleAsync(int saleId, HoldSaleRequestDto request);
    Task<SaleDto> UpdateSaleItemsAsync(int saleId, UpdateSaleItemsRequestDto request);
    Task<SaleDto> AddPaymentToHoldSaleAsync(int saleId, AddPaymentRequestDto request);
    Task<IEnumerable<SaleDto>> GetPendingSalesAsync();
    Task<SaleDto> UpdateSaleCustomerAsync(int saleId, int customerId);
    Task<(IEnumerable<CustomerDto> Items, int TotalCount)> GetCustomersAsync(string? query = null, int page = 1, int pageSize = 20, bool recentOnly = false);

    Task<CustomerDto> GetDefaultCustomerAsync();
    Task<CustomerDto> CreateCustomerAsync(CreateCustomerDto request);
    Task<CustomerDto> UpdateCustomerAsync(int id, UpdateCustomerDto request);
    Task DeleteCustomerAsync(int id);

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
