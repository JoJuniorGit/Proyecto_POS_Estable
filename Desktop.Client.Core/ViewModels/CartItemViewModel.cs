using CommunityToolkit.Mvvm.ComponentModel;
using Core.DTOs;
using Desktop.Client.Helpers;
using System;

namespace Desktop.Client.ViewModels;

public partial class CartItemViewModel : ObservableObject
{
    private readonly SaleItemDto _saleItem;
    private readonly Action _onQuantityChanged;
    private readonly Func<int, decimal, Task>? _onCommitQuantity;
    private decimal _currentExchangeRate;
    private readonly bool _isHistorical;

    private string _quantityText;
    public string QuantityText
    {
        get => _quantityText;
        set
        {
            if (SetProperty(ref _quantityText, value))
            {
                OnQuantityTextChanged(value);
                NotifyRecalculation();
            }
        }
    }

    public CartItemViewModel(SaleItemDto saleItem, Action onQuantityChanged, decimal currentRate, bool isHistorical, Func<int, decimal, Task>? onCommitQuantity = null)
    {
        _saleItem = saleItem;
        _onQuantityChanged = onQuantityChanged;
        _onCommitQuantity = onCommitQuantity;
        _currentExchangeRate = currentRate;
        _isHistorical = isHistorical;
        
        // Initialize the safe string with current value
        _quantityText = _saleItem.Quantity % 1m == 0m ? $"{_saleItem.Quantity:0.###}" : _saleItem.Quantity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    public void UpdateExchangeRate(decimal newRate)
    {
        _currentExchangeRate = newRate;
        NotifyRecalculation();
    }

    public void NotifyRecalculation()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(UnitPriceBsS));
        OnPropertyChanged(nameof(SubtotalBsS));
    }

    public SaleItemDto Model => _saleItem;

    // Passthrough properties for display
    public int Id => _saleItem.Id;
    public string ProductName => _saleItem.ProductName;
    public string DisplayProductName => _saleItem.UnitOfMeasure != Core.Entities.UnitOfMeasureType.Und
        ? $"{_saleItem.ProductName} ({_saleItem.UnitOfMeasure})"
        : _saleItem.ProductName;
    public bool IsWholesaleApplied => _saleItem.IsWholesaleApplied;
    public decimal UnitPrice => _saleItem.UnitPrice;
    public decimal UnitPriceBsS => _isHistorical 
        ? _saleItem.UnitPriceBsS 
        : PricingHelper.ToBsS(_saleItem.UnitPrice, _currentExchangeRate);
    public string SKU => "-";

    public string QuantityDisplay => _saleItem.Quantity % 1m == 0m
        ? $"{_saleItem.Quantity:0.###}"
        : $"{_saleItem.Quantity:0.000}";

    public decimal StepAmount => _saleItem.UnitOfMeasure switch
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
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal q) ||
            decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out q))
        {
            _saleItem.Quantity = Math.Round(q, 3, MidpointRounding.AwayFromZero);
        }
        else
        {
            _saleItem.Quantity = 0m;
        }

        _saleItem.Subtotal = _saleItem.Quantity * _saleItem.UnitPrice;
        _saleItem.UnitPriceBsS = UnitPriceBsS;
        _saleItem.SubtotalBsS = SubtotalBsS;

        _onQuantityChanged?.Invoke();
    }

    public void IncrementQuantity()
    {
        decimal newQty = Math.Round(_saleItem.Quantity + StepAmount, 3, MidpointRounding.AwayFromZero);
        QuantityText = newQty % 1m == 0m ? $"{newQty:0.###}" : newQty.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    public void DecrementQuantity()
    {
        decimal newQty = Math.Round(_saleItem.Quantity - StepAmount, 3, MidpointRounding.AwayFromZero);
        if (newQty <= 0m)
        {
            newQty = 0.001m;
        }
        QuantityText = newQty % 1m == 0m ? $"{newQty:0.###}" : newQty.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Dynamic calculated wrappers
    public decimal Subtotal => _saleItem.Quantity * _saleItem.UnitPrice;
    public decimal SubtotalBsS => _isHistorical 
        ? _saleItem.SubtotalBsS 
        : Subtotal * _currentExchangeRate;
}
