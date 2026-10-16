namespace Core.DTOs;

public sealed record SaleProductInfoDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
    public bool IsActive { get; init; } = true;
    public bool IsCashAdvance { get; init; }
    public decimal PriceUSD { get; init; }
    public decimal PriceBsS { get; init; }
    public decimal PriceRetailUSD { get; init; }
    public decimal PriceWholesaleUSD { get; init; }
    public decimal MinWholesaleQuantity { get; init; } = 6.000m;
    public bool HasWholesale { get; init; }
    public bool IsGroupHeader { get; init; }
    public bool IsFractional { get; init; }
    public Core.Entities.UnitOfMeasureType UnitOfMeasure { get; init; } = Core.Entities.UnitOfMeasureType.Und;
    public decimal? CostPriceUSD { get; init; }

    // 8.185-T2 (S5): override de efectivo del artículo (null = hereda el default global de
    // Pricing). SalesService resuelve la regla efectiva al agregar y la persiste en la línea.
    public RoundingRuleType? RoundingRuleCashBs { get; init; }
}
