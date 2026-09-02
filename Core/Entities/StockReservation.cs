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

    /// <summary>
    /// Id del producto variante que originó la reserva (cuando ProductId apunta al padre con stock compartido).
    /// </summary>
    public int? SourceProductId { get; set; }
    public Product? SourceProduct { get; set; }
}
