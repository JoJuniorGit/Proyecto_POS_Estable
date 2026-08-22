using CommunityToolkit.Mvvm.ComponentModel;
using Core.DTOs;
using Desktop.Client.Helpers;
using System;

namespace Desktop.Client.ViewModels;

public partial class CartItemViewModel : ObservableObject
{
    private readonly SaleItemDto _sale_item;
    private readonly Action _on_quantity_changed;
    private readonly Func<int, decimal, Task>? _on_commit_quantity;
    private decimal _current_exchange_rate;
    private readonly bool _is_historical;

    private string _quantity_text;
    public string QuantityText
    {
        get => _quantity_text;
        set
        {
            if (SetProperty(ref _quantity_text, value))
            {
                OnQuantityTextChanged(value);
                NotifyRecalculation();
            }
        }
    }

    public CartItemViewModel(SaleItemDto sale_item, Action on_quantity_changed, decimal currentRate, bool isHistorical, Func<int, decimal, Task>? on_commit_quantity = null)
    {
        _sale_item = sale_item;
        _on_quantity_changed = on_quantity_changed;
        _on_commit_quantity = on_commit_quantity;
        _current_exchange_rate = currentRate;
        _is_historical = isHistorical;
        
        // Initialize the safe string with current value
        _quantity_text = _sale_item.Quantity % 1m == 0m ? $"{_sale_item.Quantity:0.###}" : _sale_item.Quantity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    public void UpdateExchangeRate(decimal newRate)
    {
        _current_exchange_rate = newRate;
        NotifyRecalculation();
    }

    public void NotifyRecalculation()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(UnitPriceBsS));
        OnPropertyChanged(nameof(SubtotalBsS));
    }

    public SaleItemDto Model => _sale_item;

    // Passthrough properties for display
    public int Id => _sale_item.Id;
    public string ProductName => _sale_item.ProductName;
    public string DisplayProductName => _sale_item.UnitOfMeasure != Core.Entities.UnitOfMeasureType.Und
        ? $"{_sale_item.ProductName} ({_sale_item.UnitOfMeasure})"
        : _sale_item.ProductName;
    public bool IsWholesaleApplied => _sale_item.IsWholesaleApplied;
    public decimal UnitPrice => _sale_item.UnitPrice;
    public decimal UnitPriceBsS => _is_historical 
        ? _sale_item.UnitPriceBsS 
        : PricingHelper.ToBsS(_sale_item.UnitPrice, _current_exchange_rate);
    public string SKU => "-";

    public string QuantityDisplay => _sale_item.Quantity % 1m == 0m
        ? $"{_sale_item.Quantity:0.###}"
        : $"{_sale_item.Quantity:0.000}";

    public decimal StepAmount => _sale_item.UnitOfMeasure switch
    {
        Core.Entities.UnitOfMeasureType.Und => 1.0m,
        Core.Entities.UnitOfMeasureType.Kg => 0.100m,
        Core.Entities.UnitOfMeasureType.Lt => 0.100m,
        Core.Entities.UnitOfMeasureType.Grs => 100.0m,
        Core.Entities.UnitOfMeasureType.Ml => 100.0m,
        Core.Entities.UnitOfMeasureType.Lb => 0.250m,
        Core.Entities.UnitOfMeasureType.Oz => 1.0m,
        _ => 1.0m
    };

    public decimal Step => StepAmount;

    private void OnQuantityTextChanged(string value)
    {
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal _q) ||
            decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out _q))
        {
            _sale_item.Quantity = Math.Round(_q, 3, MidpointRounding.AwayFromZero);
        }
        else
        {
            _sale_item.Quantity = 0m;
        }

        _sale_item.Subtotal = _sale_item.Quantity * _sale_item.UnitPrice;
        _sale_item.UnitPriceBsS = UnitPriceBsS;
        _sale_item.SubtotalBsS = SubtotalBsS;

        _on_quantity_changed?.Invoke();
    }

    public void IncrementQuantity()
    {
        decimal newQty = Math.Round(_sale_item.Quantity + StepAmount, 3, MidpointRounding.AwayFromZero);
        QuantityText = newQty % 1m == 0m ? $"{newQty:0.###}" : newQty.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    public void DecrementQuantity()
    {
        decimal newQty = Math.Round(_sale_item.Quantity - StepAmount, 3, MidpointRounding.AwayFromZero);
        if (newQty <= 0m)
        {
            newQty = 0.001m;
        }
        QuantityText = newQty % 1m == 0m ? $"{newQty:0.###}" : newQty.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Dynamic calculated wrappers
    public decimal Subtotal => _sale_item.Quantity * _sale_item.UnitPrice;
    public decimal SubtotalBsS => _is_historical 
        ? _sale_item.SubtotalBsS 
        : Subtotal * _current_exchange_rate;
}
