using System;
using Core.Entities;

namespace CommandCenter.Tests.Builders;

public class ProductBuilder
{
    private int _id = 1;
    private string _sku = "PROD-001";
    private string _name = "Test Product";
    private decimal _costPriceUsd = 10.00m;
    private decimal _profitMarginRetail = 30.00m;
    private decimal _profitMarginWholesale = 15.00m;
    private decimal _priceRetailUsd = 0m;
    private decimal _priceWholesaleUsd = 0m;
    private decimal _stock = 100m;
    private bool _hasWholesale = false;
    private decimal _minWholesaleQuantity = 6m;
    private bool _isFractional = false;
    private UnitOfMeasureType _unitOfMeasure = UnitOfMeasureType.Und;
    private bool _isActive = true;

    public ProductBuilder WithId(int id) { _id = id; return this; }
    public ProductBuilder WithSku(string sku) { _sku = sku; return this; }
    public ProductBuilder WithName(string name) { _name = name; return this; }
    public ProductBuilder WithStock(decimal stock) { _stock = stock; return this; }
    public ProductBuilder WithCostAndMargin(decimal cost, decimal margin)
    {
        _costPriceUsd = cost;
        _profitMarginRetail = margin;
        return this;
    }
    public ProductBuilder WithManualRetailPrice(decimal price)
    {
        _priceRetailUsd = price;
        return this;
    }
    public ProductBuilder WithWholesale(decimal minQty, decimal wholesaleMargin, decimal manualWholesalePrice = 0m)
    {
        _hasWholesale = true;
        _minWholesaleQuantity = minQty;
        _profitMarginWholesale = wholesaleMargin;
        _priceWholesaleUsd = manualWholesalePrice;
        return this;
    }
    public ProductBuilder AsFractional(UnitOfMeasureType uom = UnitOfMeasureType.Kg)
    {
        _isFractional = true;
        _unitOfMeasure = uom;
        return this;
    }
    public ProductBuilder AsInactive() { _isActive = false; return this; }

    public Product Build()
    {
        decimal retail = _priceRetailUsd > 0
            ? _priceRetailUsd
            : (_costPriceUsd > 0 ? Math.Ceiling(_costPriceUsd * (1m + (_profitMarginRetail / 100m)) * 100m) / 100m : 0m);

        decimal wholesale = _hasWholesale
            ? (_priceWholesaleUsd > 0 ? _priceWholesaleUsd : (_costPriceUsd > 0 ? Math.Ceiling(_costPriceUsd * (1m + (_profitMarginWholesale / 100m)) * 100m) / 100m : retail))
            : retail;

        return new Product
        {
            Id = _id,
            SKU = _sku,
            Name = _name,
            CostPriceUSD = _costPriceUsd,
            ProfitMarginRetail = _profitMarginRetail,
            ProfitMarginWholesale = _profitMarginWholesale,
            PriceRetailUSD = retail,
            PriceWholesaleUSD = wholesale,
            PriceUSD = retail,
            StockQuantity = _stock,
            HasWholesale = _hasWholesale,
            MinWholesaleQuantity = _minWholesaleQuantity,
            IsFractional = _isFractional,
            UnitOfMeasure = _unitOfMeasure,
            IsActive = _isActive
        };
    }
}
