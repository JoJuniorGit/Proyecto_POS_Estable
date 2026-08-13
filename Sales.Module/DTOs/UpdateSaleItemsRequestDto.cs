using System.Collections.Generic;

namespace Sales.Module.DTOs;

public class UpdateSaleItemsRequestDto
{
    public List<UpdateSaleItemDto> Items { get; set; } = new();
}

public class UpdateSaleItemDto
{
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
