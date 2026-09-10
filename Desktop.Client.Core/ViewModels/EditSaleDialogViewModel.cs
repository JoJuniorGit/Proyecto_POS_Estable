using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class EditSaleDialogViewModel : ObservableObject, IDisposable
{
    private SaleDto? _sale;
    private decimal _exchangeRate = 1m;
    private IProductService? _productService;
    private CancellationTokenSource? _searchCts;

    private readonly ObservableCollection<EditableSaleItemVm> _items = new();

    [ObservableProperty]
    private string _title = "Editar Productos del Pedido";

    [ObservableProperty]
    private string _subheader = "Cliente: - | Abonado: $0.00 USD";

    [ObservableProperty]
    private string _newTotalText = "$0.00 (Bs.S 0,00)";

    [ObservableProperty]
    private string _totalPaidText = "$0.00";

    [ObservableProperty]
    private string _remainingText = "$0.00";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private bool _hasValidationError;

    [ObservableProperty]
    private bool _isSuggestionsOpen;

    [ObservableProperty]
    private IReadOnlyList<ProductQuickInfoDto> _suggestions = Array.Empty<ProductQuickInfoDto>();

    [ObservableProperty]
    private ProductQuickInfoDto? _selectedSuggestion;

    public bool HasChanges { get; private set; }

    public IEnumerable<UpdateSaleItemDto>? ModifiedItems { get; private set; }

    public ObservableCollection<EditableSaleItemVm> Items => _items;

    public EditSaleDialogViewModel()
    {
    }

    public void LoadSale(SaleDto sale, decimal exchangeRate, IProductService? productService = null)
    {
        _sale = sale;
        _exchangeRate = exchangeRate > 0 ? exchangeRate : 1m;
        _productService = productService;

        Title = $"Editar Productos del Pedido #{sale.Id}";
        string customerName = sale.CustomerName ?? sale.Customer?.Name ?? "Consumidor Final";
        string customerCedula = sale.CustomerCedula ?? sale.Customer?.CedulaOrRif ?? "-";
        Subheader = $"Cliente: {customerName} ({customerCedula}) | Total Abonado: ${sale.TotalPaidUSD:N2} USD";

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
        itemVm.OnQuantityChangedAction = RefreshSummary;
        itemVm.OnRemoveAction = () =>
        {
            _items.Remove(itemVm);
            RefreshSummary();
        };
    }

    [RelayCommand]
    private void SelectSuggestion()
    {
        if (SelectedSuggestion == null) return;

        var selectedProduct = SelectedSuggestion;
        IsSuggestionsOpen = false;
        SearchText = string.Empty;
        SelectedSuggestion = null;

        var existing = _items.FirstOrDefault(i => i.ProductId == selectedProduct.Id);
        if (existing != null)
        {
            existing.Quantity += 1m;
        }
        else
        {
            var newItem = new EditableSaleItemVm
            {
                SaleItemId = 0,
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

    partial void OnSearchTextChanged(string value)
    {
        _ = SearchProductsAsync(value);
    }

    private async Task SearchProductsAsync(string query)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query) || _productService == null)
            {
                IsSuggestionsOpen = false;
                return;
            }

            try
            {
                _searchCts?.Cancel();
                _searchCts?.Dispose();
            }
            catch (ObjectDisposedException) { }

            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            await Task.Delay(200, token);
            if (token.IsCancellationRequested) return;

            var suggestions = await _productService.GetSuggestionsAsync(query, activeOnly: true, token);
            if (token.IsCancellationRequested) return;

            var validSuggestions = suggestions?.Where(s => s.Id > 0).ToList() ?? new List<ProductQuickInfoDto>();

            Suggestions = validSuggestions;
            IsSuggestionsOpen = validSuggestions.Any() && SearchText.Trim().Length > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogCrash(ex, "EditSaleDialogViewModel.SearchProductsAsync");
            IsSuggestionsOpen = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        HasChanges = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Save()
    {
        if (_sale == null)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        HasValidationError = false;
        ValidationMessage = string.Empty;

        decimal newTotalUsd = _items.Sum(i => i.Subtotal);
        if (newTotalUsd < _sale.TotalPaidUSD - 0.05m)
        {
            HasValidationError = true;
            ValidationMessage = $"El nuevo total del pedido (${newTotalUsd:N2}) no puede ser menor al monto ya abonado por el cliente (${_sale.TotalPaidUSD:N2}).";
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
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshSummary()
    {
        if (_sale == null) return;

        decimal newTotalUsd = _items.Sum(i => i.Subtotal);
        decimal newTotalBsS = Math.Round(newTotalUsd * _exchangeRate, 2, MidpointRounding.AwayFromZero);
        decimal remainingUsd = Math.Max(0m, newTotalUsd - _sale.TotalPaidUSD);

        NewTotalText = $"${newTotalUsd:N2} (Bs.S {newTotalBsS:N2})";
        TotalPaidText = $"${_sale.TotalPaidUSD:N2}";
        RemainingText = $"${remainingUsd:N2}";
    }

    public event EventHandler? CloseRequested;

    public void Dispose()
    {
        var oldCts = Interlocked.Exchange(ref _searchCts, null);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}