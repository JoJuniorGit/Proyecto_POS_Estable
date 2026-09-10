using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Desktop.Client.ViewModels;

public partial class EditableSaleItemVm : ObservableObject
{
    public int SaleItemId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPriceRetailUSD { get; set; }
    public decimal UnitPriceWholesaleUSD { get; set; }
    public decimal MinWholesaleQuantity { get; set; }
    public bool IsWholesaleSale { get; set; }
    public decimal ExchangeRate { get; set; } = 1m;

    private decimal _quantity = 1m;
    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (value < 0.01m && value != 0m) value = 0.01m;
            if (SetProperty(ref _quantity, value))
            {
                OnPropertyChanged(nameof(UnitPrice));
                OnPropertyChanged(nameof(UnitPriceBsS));
                OnPropertyChanged(nameof(Subtotal));
                OnPropertyChanged(nameof(SubtotalBsS));
                OnQuantityChangedAction?.Invoke();
            }
        }
    }

    public Action? OnQuantityChangedAction { get; set; }
    public Action? OnRemoveAction { get; set; }

    public decimal UnitPrice
    {
        get
        {
            if (IsWholesaleSale && MinWholesaleQuantity > 0 && Quantity >= MinWholesaleQuantity && UnitPriceWholesaleUSD > 0)
            {
                return UnitPriceWholesaleUSD;
            }
            return UnitPriceRetailUSD;
        }
    }

    public decimal UnitPriceBsS => Math.Round(UnitPrice * ExchangeRate, 2, MidpointRounding.AwayFromZero);
    public decimal Subtotal => Quantity * UnitPrice;
    public decimal SubtotalBsS => Math.Round(Subtotal * ExchangeRate, 2, MidpointRounding.AwayFromZero);

    public IRelayCommand IncreaseQuantityCommand { get; }
    public IRelayCommand DecreaseQuantityCommand { get; }
    public IRelayCommand RemoveItemCommand { get; }

    public EditableSaleItemVm()
    {
        IncreaseQuantityCommand = new RelayCommand(() => Quantity += 1m);
        DecreaseQuantityCommand = new RelayCommand(() =>
        {
            if (Quantity > 1m) Quantity -= 1m;
        });
        RemoveItemCommand = new RelayCommand(() => OnRemoveAction?.Invoke());
    }
}