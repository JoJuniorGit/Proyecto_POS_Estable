namespace Core.DTOs;

public sealed record CreateSystemProductRequest
{
    public string Name { get; init; } = string.Empty;
    public string SKU { get; init; } = string.Empty;
    public decimal PriceRetailUSD { get; init; }
    public decimal StockQuantity { get; init; }
    public bool IsCashAdvance { get; init; }
    public bool IsActive { get; init; } = true;
}
