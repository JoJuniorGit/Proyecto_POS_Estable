using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;

namespace Desktop.Client.Services;

// ── Retiros Pendientes (Mercancía en Custodia) ──
public class PendingPickupClientDto
{
    public int SaleId { get; set; }
    public int? InvoiceNumber { get; set; }
    public DateTime Date { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerCedula { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public decimal TotalUSD { get; set; }
    public decimal TotalBsS { get; set; }
    public string DeliveryStatus { get; set; } = "PendingPickup";
    public List<PendingPickupItemDto> Items { get; set; } = new();
}

public class PendingPickupItemDto
{
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPriceBsS { get; set; }
    public decimal SubtotalBsS { get; set; }
}

public class SalePaymentDto
{
    public int PaymentMethodId { get; set; }
    public decimal Amount { get; set; }
    public decimal AmountBsS { get; set; }
    public decimal AmountLocal { get; set; }
    public string? ReferenceNumber { get; set; }

    public SalePaymentDto() { }
    public SalePaymentDto(int paymentMethodId, decimal amount, decimal amountBsS, string? referenceNumber)
    {
        PaymentMethodId = paymentMethodId;
        Amount = amount;
        AmountBsS = amountBsS;
        AmountLocal = amountBsS;
        ReferenceNumber = referenceNumber;
    }
}

public class SaleHistoryDto
{
    public int Id { get; set; }
    public int? InvoiceNumber { get; set; }
    public DateTime Date { get; set; }
    public decimal TotalUSD { get; set; }
    public decimal AppliedRate { get; set; }
    public decimal TotalBsS { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal FinalPaidAmountBsS { get; set; }
    public int? CashierId { get; set; }
    public string CashierName { get; set; } = "Usuario Desconocido";
    public string? CustomerName { get; set; }
    public string? CustomerCedula { get; set; }
    public List<SaleItemHistoryDto> Items { get; set; } = new();
    public List<PaymentDetailDto> Payments { get; set; } = new();
}

public class PaymentDetailDto
{
    public string MethodName { get; set; } = string.Empty;
    public decimal AmountBsS { get; set; }
    public string? Reference { get; set; }
}

public class SaleItemHistoryDto
{
    public int Id { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal UnitPriceBsS { get; set; }
    public decimal SubtotalBsS { get; set; }
}

public interface ISalesService
{
    SaleDto? CurrentSale { get; }
    Task<SaleDto> GetSaleAsync(int saleId);
    Task<SaleDto> StartSaleAsync(int? cashierId = null);
    Task<SaleDto> AddItemAsync(int sale_id, int product_id, decimal quantity, decimal exchange_rate, decimal? custom_unit_price_usd = null, decimal? custom_unit_price_bs_s = null);
    Task<SaleDto> RemoveItemAsync(int sale_id, int item_id, decimal exchange_rate);
    Task<SaleDto> UpdateItemQuantityAsync(int sale_id, int item_id, decimal quantity, decimal exchange_rate);
    Task<SaleDto> UpdateExchangeRateAsync(int sale_id, decimal exchange_rate);
    Task<SaleDto> UpdatePriceListAsync(int saleId, string priceListType);
    Task<int> CompleteSaleAsync(int sale_id, decimal exchange_rate, IEnumerable<SalePaymentDto> payments, decimal rounding_adjustment = 0, int? cashierId = null, bool isPendingPickup = false);
    Task<(IEnumerable<SaleHistoryDto> Items, int TotalCount)> GetSalesHistoryAsync(int page, int page_size, System.DateTime? start_date = null, System.DateTime? end_date = null, string? search = null, System.Threading.CancellationToken cancellation_token = default);
    Task<SaleHistoryDto> GetSaleHistoryDetailAsync(int sale_id, System.Threading.CancellationToken cancellation_token = default);
    Task<SaleDto> HoldSaleAsync(int saleId, HoldSaleRequestDto request);
    Task<SaleDto> AddPaymentToHoldSaleAsync(int saleId, AddPaymentRequestDto request);
    Task<IEnumerable<SaleDto>> GetPendingSalesAsync();
    Task<SaleDto> UpdateSaleCustomerAsync(int saleId, int customerId);
    Task<(IEnumerable<CustomerDto> Items, int TotalCount)> GetCustomersAsync(string? query = null, int page = 1, int pageSize = 20, bool recentOnly = false);

    Task<CustomerDto> GetDefaultCustomerAsync();
    Task<CustomerDto> CreateCustomerAsync(CreateCustomerDto request);
    Task<CustomerDto> UpdateCustomerAsync(int id, UpdateCustomerDto request);
    Task DeleteCustomerAsync(int id);

    Task<IEnumerable<PendingPickupClientDto>> GetPendingPickupsAsync();
    Task ConfirmPickupAsync(int saleId);
    Task UpdateSaleItemsAsync(int saleId, IEnumerable<UpdateSaleItemDto> items, decimal exchangeRate);
}

/// <summary>DTO for updating an item quantity in a pending/OnHold sale.</summary>
public class UpdateSaleItemDto
{
    public int SaleItemId { get; set; }
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}


