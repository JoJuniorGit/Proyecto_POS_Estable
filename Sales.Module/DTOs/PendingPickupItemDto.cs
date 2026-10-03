namespace Sales.Module.DTOs;

public class PendingPickupItemDto
{
    public int SaleItemId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal DeliveredQuantity { get; set; }
    public decimal PendingQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal UnitPriceBsS { get; set; }
    public decimal SubtotalBsS { get; set; }
    public bool IsCustomPrice { get; set; }
}
