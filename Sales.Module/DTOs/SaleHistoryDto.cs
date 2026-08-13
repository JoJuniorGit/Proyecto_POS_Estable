using System;
using System.Collections.Generic;

namespace Sales.Module.DTOs;

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
    public string DeliveryStatus { get; set; } = "Delivered";
    public DateTime? PickupDate { get; set; }
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
