using System;

namespace Core.Entities;

public class StockMovement : BaseEntity
{
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public int QuantityChange { get; set; }
    public int NewStockLevel { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime MovementDate { get; set; } = DateTime.UtcNow;

    // Optional: UserId if we want to track who made the change
    public string? UserId { get; set; }
}
