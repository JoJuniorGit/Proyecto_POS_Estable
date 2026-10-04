using System;
using System.Collections.Generic;

namespace Sales.Module.DTOs;

public class PartialDeliveryRequest
{
    public List<PartialDeliveryItemRequest> Items { get; set; } = new();
    public string? Notes { get; set; }
}

public class PartialDeliveryItemRequest
{
    public int SaleItemId { get; set; }
    public decimal Quantity { get; set; }
}

public class DeliveryReceiptDto
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
    public List<DeliveryReceiptItemDto> Items { get; set; } = new();
}

public class DeliveryReceiptItemDto
{
    public int SaleItemId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal QuantityDelivered { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal UnitPriceBsS { get; set; }
    public decimal SubtotalBsS { get; set; }
}
