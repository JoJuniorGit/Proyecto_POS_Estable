using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Core.Entities;

namespace Sales.Module.Entities;

public enum SaleStatus
{
    Pending,
    Completed,
    Cancelled,
    OnHold
}

public class Sale
{
    public int Id { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    
    [MaxLength(150)]
    public string? CustomerName { get; set; }
    
    [MaxLength(20)]
    public string? CustomerCedula { get; set; }
    
    public SaleDeliveryStatus DeliveryStatus { get; set; } = SaleDeliveryStatus.Delivered;
    public DateTime? PickupDate { get; set; }

    [MaxLength(20)]
    public string PriceListType { get; set; } = "Retail";

    /// <summary>
    /// Consecutive invoice number assigned only upon sale completion.
    /// Independent of the PK to avoid gaps from pending/cancelled sales.
    /// </summary>
    public int? InvoiceNumber { get; set; }

    public DateTime Date { get; set; } = DateTime.UtcNow;

    [Required]
    public SaleStatus Status { get; set; } = SaleStatus.Pending;

    [Column(TypeName = "decimal(18,4)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalUSD { get; set; }
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal AppliedRate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalBsS { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal SubtotalBsS { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RoundingAdjustment { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FinalPaidAmountBsS { get; set; }

    public int? CashierId { get; set; }
    public User? Cashier { get; set; }

    public bool IsZeroAmountOrder => TotalUSD == 0;

    public List<SalePayment> Payments { get; set; } = new();

    // Navigation property
    public List<SaleItem> Items { get; set; } = new();
}
