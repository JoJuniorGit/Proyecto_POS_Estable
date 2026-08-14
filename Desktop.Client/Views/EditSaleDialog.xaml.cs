using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Desktop.Client.Services;

namespace Desktop.Client.Views;

public partial class EditSaleDialog : Window
{
    private SaleDto? _sale;
    private decimal _exchangeRate = 1m;
    private IProductService? _productService;
    private CancellationTokenSource? _searchCts;

    private readonly ObservableCollection<EditableSaleItemVm> _items = new();

    public bool HasChanges { get; private set; }
    public IEnumerable<UpdateSaleItemDto>? ModifiedItems { get; private set; }

    public EditSaleDialog()
    {
        InitializeComponent();
        GridItems.ItemsSource = _items;
    }

    public void LoadSale(SaleDto sale, decimal exchangeRate, IProductService? productService = null)
    {
        _sale = sale;
        _exchangeRate = exchangeRate > 0 ? exchangeRate : 1m;
        _productService = productService;

        TxtTitle.Text = $"Editar Productos del Pedido #{sale.Id}";
        string customerName = sale.CustomerName ?? sale.Customer?.Name ?? "Consumidor Final";
        string customerCedula = sale.CustomerCedula ?? sale.Customer?.CedulaOrRif ?? "-";
        TxtSubheader.Text = $"Cliente: {customerName} ({customerCedula}) | Total Abonado: ${sale.TotalPaidUSD:N2} USD";

        _items.Clear();
        if (sale.Items != null)
        {
            foreach (var item in sale.Items)
            {
                var itemVm = new EditableSaleItemVm
                {
                    SaleItemId = item.Id,
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    UnitPriceRetailUSD = item.UnitPrice,
                    UnitPriceWholesaleUSD = item.UnitPrice,
                    Quantity = item.Quantity,
                    ExchangeRate = _exchangeRate,
                    IsWholesaleSale = string.Equals(sale.PriceListType, "Wholesale", StringComparison.OrdinalIgnoreCase)
                };

                AttachItemEvents(itemVm);
                _items.Add(itemVm);
            }
        }

        RefreshSummary();
    }

    private void AttachItemEvents(EditableSaleItemVm itemVm)
    {
        itemVm.OnQuantityChangedAction = () => RefreshSummary();
        itemVm.OnRemoveAction = () =>
        {
            _items.Remove(itemVm);
            RefreshSummary();
        };
    }

    private async void TxtProductSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = TxtProductSearch.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            PopupSuggestions.IsOpen = false;
            return;
        }

        if (_productService == null)
        {
            PopupSuggestions.IsOpen = false;
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            await Task.Delay(200, token);
            if (token.IsCancellationRequested) return;

            var suggestions = await _productService.GetSuggestionsAsync(query, activeOnly: true, token);
            if (token.IsCancellationRequested) return;

            var validSuggestions = suggestions?.Where(s => s.Id > 0).ToList() ?? new List<ProductQuickInfoDto>();

            Dispatcher.Invoke(() =>
            {
                if (validSuggestions.Any() && TxtProductSearch.Text.Trim().Length > 0)
                {
                    LstSuggestions.ItemsSource = validSuggestions;
                    PopupSuggestions.IsOpen = true;
                }
                else
                {
                    PopupSuggestions.IsOpen = false;
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditSaleDialog] Search error: {ex.Message}");
            Dispatcher.Invoke(() => PopupSuggestions.IsOpen = false);
        }
    }


    private void LstSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstSuggestions.SelectedItem is ProductQuickInfoDto selectedProduct)
        {
            PopupSuggestions.IsOpen = false;
            TxtProductSearch.Text = string.Empty;
            LstSuggestions.SelectedItem = null;

            // Check if product is already in items list
            var existing = _items.FirstOrDefault(i => i.ProductId == selectedProduct.Id);
            if (existing != null)
            {
                existing.Quantity += 1m;
            }
            else
            {
                var newItem = new EditableSaleItemVm
                {
                    SaleItemId = 0, // New item
                    ProductId = selectedProduct.Id,
                    ProductName = selectedProduct.Name,
                    UnitPriceRetailUSD = selectedProduct.PriceUSD,
                    UnitPriceWholesaleUSD = selectedProduct.PriceWholesaleUSD > 0 ? selectedProduct.PriceWholesaleUSD : selectedProduct.PriceUSD,
                    MinWholesaleQuantity = selectedProduct.MinWholesaleQuantity,
                    Quantity = 1m,
                    ExchangeRate = _exchangeRate,
                    IsWholesaleSale = string.Equals(_sale?.PriceListType, "Wholesale", StringComparison.OrdinalIgnoreCase)
                };

                AttachItemEvents(newItem);
                _items.Add(newItem);
            }

            RefreshSummary();
        }
    }

    private void RefreshSummary()
    {
        if (_sale == null) return;

        decimal newTotalUsd = _items.Sum(i => i.Subtotal);
        decimal newTotalBsS = Math.Round(newTotalUsd * _exchangeRate, 2, MidpointRounding.AwayFromZero);
        decimal remainingUsd = Math.Max(0m, newTotalUsd - _sale.TotalPaidUSD);

        TxtNewTotal.Text = $"${newTotalUsd:N2} (Bs.S {newTotalBsS:N2})";
        TxtTotalPaid.Text = $"${_sale.TotalPaidUSD:N2}";
        TxtRemaining.Text = $"${remainingUsd:N2}";
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        HasChanges = false;
        DialogResult = false;
        Close();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_sale == null)
        {
            DialogResult = false;
            Close();
            return;
        }

        decimal newTotalUsd = _items.Sum(i => i.Subtotal);
        if (newTotalUsd < _sale.TotalPaidUSD - 0.05m)
        {
            MessageBox.Show(
                $"El nuevo total del pedido (${newTotalUsd:N2}) no puede ser menor al monto ya abonado por el cliente (${_sale.TotalPaidUSD:N2}).",
                "Restricción del Pedido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ModifiedItems = _items.Select(i => new UpdateSaleItemDto
        {
            SaleItemId = i.SaleItemId,
            ProductId = i.ProductId,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice
        }).ToList();

        HasChanges = true;
        DialogResult = true;
        Close();
    }
}

/// <summary>VM wrapper for an editable sale item row in EditSaleDialog.</summary>
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
