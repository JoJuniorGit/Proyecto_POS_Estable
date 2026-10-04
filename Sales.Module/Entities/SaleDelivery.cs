using System;
using System.Collections.Generic;

namespace Sales.Module.Entities;

public class SaleDelivery
{
    public int Id { get; set; }
    public int SaleId { get; set; }
    public DateTime DeliveredAt { get; set; }
    public int? DeliveredByUserId { get; set; }
    public string DeliveredByName { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<SaleDeliveryItem> Items { get; set; } = new();
}
