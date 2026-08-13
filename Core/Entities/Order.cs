using System.Collections.Generic;

namespace Core.Entities;

public enum OrderStatus
{
    Pending,
    Paid,
    Preparing,
    OutForDelivery,
    Delivered,
    Cancelled
}

public class Order : BaseEntity
{
    public string OrderNumber { get; set; } = string.Empty; // Unique human-readable ID
    public decimal TotalAmount { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public int? CashierId { get; set; } // User who processed the sale
    public int? DriverId { get; set; } // Assigned driver
    
    // For Delivery
    public string? CustomerName { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? CustomerPhone { get; set; }

    public List<OrderItem> Items { get; set; } = new();
}

public class OrderItem : BaseEntity
{
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty; // Snapshot of name at time of sale
    public decimal UnitPrice { get; set; } // Snapshot of price
    public int Quantity { get; set; }
    public decimal TotalLine => UnitPrice * Quantity;
}
