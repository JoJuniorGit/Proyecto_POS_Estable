using System;
using System.Collections.Generic;
using System.Threading;
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
    public decimal TotalUnits { get; set; }
    public decimal DeliveredUnits { get; set; }
    public string DeliveryStatus { get; set; } = "PendingPickup";
    public List<PendingPickupItemDto> Items { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public PartialDeliveryDialogResult? PendingDraft { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPartiallyDelivered => string.Equals(DeliveryStatus, "PartiallyDelivered", StringComparison.Ordinal);

    [System.Text.Json.Serialization.JsonIgnore]
    public string DeliveryStatusLabel => IsPartiallyDelivered ? "Entrega Parcial" : "Pendiente de Retiro";

    [System.Text.Json.Serialization.JsonIgnore]
    public string ProgressText => $"Retirado: {DeliveredUnits:0.###}/{TotalUnits:0.###}";

    [System.Text.Json.Serialization.JsonIgnore]
    public double ProgressPercent => TotalUnits <= 0m
        ? 0d
        : decimal.ToDouble(Math.Clamp(DeliveredUnits / TotalUnits * 100m, 0m, 100m));
}

public class PendingPickupItemDto
{
    public int SaleItemId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal DeliveredQuantity { get; set; }
    public decimal PendingQuantity { get; set; }
    public decimal UnitPriceBsS { get; set; }
    public decimal SubtotalBsS { get; set; }
}

public class DeliveryReceiptClientDto
{
    public int DeliveryId { get; set; }
    public int SaleId { get; set; }
    public int? InvoiceNumber { get; set; }
    public DateTime DeliveredAt { get; set; }
    public string DeliveredByName { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public string? CustomerCedula { get; set; }
    public string DeliveryStatus { get; set; } = string.Empty;
    public decimal TotalUnits { get; set; }
    public decimal DeliveredUnits { get; set; }
    public decimal PendingUnits { get; set; }
    public List<DeliveryReceiptItemClientDto> Items { get; set; } = new();
}

public class DeliveryReceiptItemClientDto
{
    public int SaleItemId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal QuantityDelivered { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal UnitPriceBsS { get; set; }
    public decimal SubtotalBsS { get; set; }
}

public class PartialDeliveryItemRequestDto
{
    public int SaleItemId { get; set; }
    public decimal Quantity { get; set; }
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

/// <summary>8.156 (CLEAN-05): respuesta canónica de /api/sales/{id}/checkout-preview — redondeo
/// fiscal, saldo, vuelto y estado de pago total calculados por el servidor (espejo del web).</summary>
public class CheckoutPreviewClientDto
{
    public decimal TotalUSD { get; set; }
    public decimal TotalBsS { get; set; }
    public decimal TotalPaidUSD { get; set; }
    public decimal TotalPaidBsS { get; set; }
    public decimal RemainingBalanceUSD { get; set; }
    public decimal RemainingBalanceBsS { get; set; }
    public decimal RoundingAdjustment { get; set; }
    public decimal ChangeDueUSD { get; set; }
    public decimal ChangeDueBsS { get; set; }
    public bool IsFullyPaid { get; set; }
}

public class SaleHistoryDto
{
    public int Id { get; set; }
    public int? InvoiceNumber { get; set; }
    public DateTime Date { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime DateLocal => Date.Kind == DateTimeKind.Utc
        ? Core.Helpers.TimeZoneHelper.ToVenezuelaTime(Date)
        : Date;
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
    Task<SaleDto> AddItemAsync(int saleId, int productId, decimal quantity, decimal exchangeRate, decimal? customUnitPriceUsd = null, decimal? customUnitPriceBsS = null, string? authorizationToken = null);
    Task<SaleDto> RemoveItemAsync(int saleId, int itemId, decimal exchangeRate);
    Task<SaleDto> UpdateItemQuantityAsync(int saleId, int itemId, decimal quantity, decimal exchangeRate);
    Task<SaleDto> UpdateExchangeRateAsync(int saleId, decimal exchangeRate);
    Task<SaleDto> UpdatePriceListAsync(int saleId, string priceListType);
    Task<int> CompleteSaleAsync(int saleId, decimal exchangeRate, IEnumerable<SalePaymentDto> payments, decimal roundingAdjustment = 0, int? cashierId = null, bool isPendingPickup = false, string? idempotencyKey = null, System.Threading.CancellationToken cancellationToken = default);
    Task<CheckoutPreviewClientDto> GetCheckoutPreviewAsync(int saleId, decimal exchangeRate, IEnumerable<SalePaymentDto> payments);
    Task<(IEnumerable<SaleHistoryDto> Items, int TotalCount)> GetSalesHistoryAsync(int page, int pageSize, System.DateTime? startDate = null, System.DateTime? endDate = null, string? search = null, System.Threading.CancellationToken cancellationToken = default);
    Task<SaleHistoryDto> GetSaleHistoryDetailAsync(int saleId, System.Threading.CancellationToken cancellationToken = default);
    Task<SaleDto> HoldSaleAsync(int saleId, HoldSaleRequestDto request);
    Task<SaleDto> AddPaymentToHoldSaleAsync(int saleId, AddPaymentRequestDto request);
    Task<SaleDto> ClaimSaleAsync(int saleId, string action, System.Threading.CancellationToken cancellationToken = default);
    Task<SaleDto> ReleaseSaleAsync(int saleId, bool force = false, System.Threading.CancellationToken cancellationToken = default);
    Task<(IEnumerable<SaleDto> Items, int TotalCount)> GetPendingSalesPagedAsync(int limit = 200, int offset = 0);
    Task<SaleDto> UpdateSaleCustomerAsync(int saleId, int customerId);
    Task<(IEnumerable<CustomerDto> Items, int TotalCount)> GetCustomersAsync(string? query = null, int page = 1, int pageSize = 20, bool recentOnly = false);

    Task<CustomerDto> GetDefaultCustomerAsync();
    Task<CustomerDto> CreateCustomerAsync(CreateCustomerDto request);
    Task<CustomerDto> UpdateCustomerAsync(int id, UpdateCustomerDto request);
    Task DeleteCustomerAsync(int id);

    Task<(IEnumerable<PendingPickupClientDto> Items, int TotalCount)> GetPendingPickupsPagedAsync(int limit = 200, int offset = 0);
    Task ConfirmPickupAsync(int saleId);
    Task<DeliveryReceiptClientDto> DeliverPartialAsync(
        int saleId,
        IReadOnlyList<PartialDeliveryItemRequestDto> items,
        string? notes,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
    Task<byte[]> GetDeliveryNoteAsync(int saleId, int deliveryId, CancellationToken cancellationToken = default);
    Task UpdateSaleItemsAsync(int saleId, IEnumerable<UpdateSaleItemDto> items, decimal exchangeRate);
    Task<byte[]?> GetReceiptAsync(int saleId);
}

/// <summary>DTO for updating an item quantity in a pending/OnHold sale.</summary>
public class UpdateSaleItemDto
{
    public int SaleItemId { get; set; }
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}


