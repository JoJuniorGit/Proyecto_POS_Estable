namespace Sales.Module.Entities;

public class SaleDeliveryItem
{
    public int Id { get; set; }
    public int SaleDeliveryId { get; set; }
    public int SaleItemId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal QuantityDelivered { get; set; }
}
