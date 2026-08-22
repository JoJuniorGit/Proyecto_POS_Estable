using System;

namespace Core.Entities;

public class StockReservation : BaseEntity
{
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public decimal Quantity { get; set; }
    public DateTime ExpiryDate { get; set; }
    public bool IsConfirmed { get; set; } = false;

    // Optional: Reference to an OrderId or CartId if available
    public string? ReferenceId { get; set; }
}
