using System;
using System.Collections.Generic;

namespace Sales.Module.DTOs;

public class PendingPickupDto
{
    public int SaleId { get; set; }
    public int? InvoiceNumber { get; set; }
    public DateTime Date { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerCedula { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public decimal TotalUSD { get; set; }
    public decimal TotalBsS { get; set; }
    public string DeliveryStatus { get; set; } = "PendingPickup";
    public DateTime? PickupDate { get; set; }
    public List<SaleItemHistoryDto> Items { get; set; } = new();
}
