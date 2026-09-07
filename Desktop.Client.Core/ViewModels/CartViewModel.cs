using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.Common;
using Core.DTOs;
using Desktop.Client.Helpers;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

/// <summary>
/// ViewModel dedicated to managing the shopping cart state and operations.
/// Subscribes to exchange rate changes to provide real-time price updates.
/// </summary>
public partial class CartViewModel : ObservableObject, System.IDisposable
{
    private readonly ISalesService _salesService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly IDialogService? _dialogService;

    public CartViewModel(ISalesService salesService, IExchangeRateService exchangeRateService, IDialogService? dialogService = null)
    {
        _salesService = salesService;
        _exchangeRateService = exchangeRateService;
        _dialogService = dialogService;

        // Reactive sync: When the rate changes, update all items and totals at once.
        WeakReferenceMessenger.Default.Register<ExchangeRateChangedMessage>(this, (r, m) =>
        {
            UpdateAllPrices(m.Value);
        });

        // Reactive sync: When active sale changes in SalesService, update Cart state
        WeakReferenceMessenger.Default.Register<CurrentSaleChangedMessage>(this, (r, m) =>
        {
            CurrentSale = m.Value;
        });

        // Reactive sync: When OnHold sales are recalculated on server, refresh if current sale is OnHold
        WeakReferenceMessenger.Default.Register<OnHoldSalesRefreshMessage>(this, async (r, m) =>
        {
            if (CurrentSale != null && CurrentSale.Status == "OnHold")
            {
                try
                {
                    var updated = await _salesService.GetSaleAsync(CurrentSale.Id);
                    CurrentSale = updated;
                }
                catch
                {
                    // Ignore transient network errors during background refresh
                }
            }
        });
    }

    private ObservableCollection<CartItemViewModel> _cartItems = new();
    public ObservableCollection<CartItemViewModel> CartItems
    {
        get => _cartItems;
        private set => SetProperty(ref _cartItems, value);
    }

    private CartItemViewModel? _selectedSaleItem;
    public CartItemViewModel? SelectedSaleItem
    {
        get => _selectedSaleItem;
        set => SetProperty(ref _selectedSaleItem, value);
    }

    private decimal _totalUsd;
    public decimal TotalUSD
    {
        get => _totalUsd;
        set => SetProperty(ref _totalUsd, value);
    }

    private decimal _subtotal;
    public decimal Subtotal
    {
        get => _subtotal;
        set => SetProperty(ref _subtotal, value);
    }

    private bool _isEmpty = true;
    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }

    public decimal SubtotalLocal => CurrentSale != null && CurrentSale.Status != "Pending"
        ? CurrentSale.SubtotalBsS 
        : PricingHelper.ToBsS(Subtotal, _exchangeRateService.CurrentRate);
        
    public decimal TotalAmountLocal => CurrentSale != null && CurrentSale.Status != "Pending"
        ? CurrentSale.TotalBsS 
        : PricingHelper.ToBsS(TotalUSD, _exchangeRateService.CurrentRate);

    private SaleDto? _currentSale;
    public SaleDto? CurrentSale
    {
        get => _currentSale;
        set
        {
            if (SetProperty(ref _currentSale, value))
            {
                OnPropertyChanged(nameof(CustomerName));
                OnPropertyChanged(nameof(CustomerCedula));
                OnPropertyChanged(nameof(PriceListType));
                OnPropertyChanged(nameof(IsWholesalePriceList));
                UpdateCollection();
            }
        }
    }

    public string CustomerName => _currentSale?.CustomerName ?? "Consumidor Final";
    public string CustomerCedula => _currentSale?.CustomerCedula ?? "V-00000000";
    public string PriceListType => _currentSale?.PriceListType ?? "Retail";
    public bool IsWholesalePriceList => PriceListType == "Wholesale";

    [RelayCommand]
    public async Task SetPriceListAsync(string type)
    {
        if (CurrentSale == null || string.Equals(CurrentSale.PriceListType, type, System.StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var updated = await _salesService.UpdatePriceListAsync(CurrentSale.Id, type);
            CurrentSale = updated;
        }
        catch (System.Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowWarning("Lista de Precios", ex.Message);
            else if (Application.Current != null) MessageBox.Show(ex.Message, "Lista de Precios", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void UpdateCollection()
    {
        void DoUpdate()
        {
            if (CurrentSale != null)
            {
                var idToRestore = SelectedSaleItem?.Id;
                
                CartItems.Clear();
                decimal rateToUse = CurrentSale.AppliedRate > 0 ? CurrentSale.AppliedRate : _exchangeRateService.CurrentRate;
                bool isHistorical = CurrentSale.Status != "Pending";
                foreach (var item in CurrentSale.Items)
                {
                    CartItems.Add(new CartItemViewModel(item, RecalculateTotals, rateToUse, isHistorical, CommitItemQuantityAsync));
                }

                RecalculateTotals();
                IsEmpty = !CartItems.Any();

                if (idToRestore.HasValue)
                    SelectedSaleItem = CartItems.FirstOrDefault(i => i.Id == idToRestore.Value);
            }
            else
            {
                CartItems.Clear();
                RecalculateTotals();
                IsEmpty = true;
            }
        }

        UiThreadMarshaller.Invoke(DoUpdate);
    }

    private void RecalculateTotals()
    {
        decimal newSubtotal = CartItems.Sum(c => c.Subtotal);
        Subtotal = newSubtotal;
        TotalUSD = Subtotal;

        if (CurrentSale != null)
        {
            CurrentSale.TotalUSD = TotalUSD;
            CurrentSale.Subtotal = Subtotal;
            // We NO LONGER overwrite the backend's precise BsS calculations (CurrentSale.TotalBsS / SubtotalBsS)
            // with client-side recalculations, as this caused the price explosion bug.
        }

        OnPropertyChanged(nameof(TotalAmountLocal));
        OnPropertyChanged(nameof(SubtotalLocal));

        // Notify other components (like Checkout) that the cart has changed
        WeakReferenceMessenger.Default.Send(new CartUpdatedMessage(TotalUSD));
    }

    /// <summary>
    /// Mass notification pattern to refresh all prices when the exchange rate changes.
    /// Skips update if the cart is displaying a historical sale (Completed/Cancelled) with a frozen AppliedRate.
    /// Re-fetches OnHold sales from server to get accurate recalculated totals and items.
    /// </summary>
    public void UpdateAllPrices(decimal newRate)
    {
        if (CurrentSale != null)
        {
            if (CurrentSale.Status == "OnHold")
            {
                // 8.9-M14: sin Task.Run para I/O pura (GetSaleAsync es async, no bloquea);
                // se despacha en background implícito del await con fire-and-forget sancionado.
                RefreshOnHoldSaleAsync().SafeFireAndForget("CartViewModel.UpdateAllPrices");
                return;
            }

            // Guard: Do NOT overwrite completed or cancelled sale rates.
            if (CurrentSale.Status != "Pending")
                return;
        }

        foreach (var item in CartItems)
        {
            item.UpdateExchangeRate(newRate);
        }
        
        // Fire notifications for calculated totals
        OnPropertyChanged(nameof(TotalAmountLocal));
        OnPropertyChanged(nameof(SubtotalLocal));
    }

    private async Task RefreshOnHoldSaleAsync()
    {
        try
        {
            var updated = await _salesService.GetSaleAsync(CurrentSale!.Id);
            UiThreadMarshaller.Invoke(() => CurrentSale = updated);
        }
        catch
        {
            // Ignorar errores de red transitorios (misma semántica que el código previo).
        }
    }

    [RelayCommand]
    private async Task IncreaseQuantity(CartItemViewModel? vm)
    {
        if (CurrentSale == null || vm == null) return;
        try
        {
            int selectedId = vm.Id;
            decimal newQty = Math.Round(vm.Model.Quantity + vm.StepAmount, 3, MidpointRounding.AwayFromZero);
            CurrentSale = await _salesService.UpdateItemQuantityAsync(CurrentSale.Id, vm.Id, newQty, _exchangeRateService.CurrentRate);
            SelectedSaleItem = CartItems.FirstOrDefault(i => i.Id == selectedId);
        }
        catch (System.Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowWarning("Error", ex.Message);
            else if (Application.Current != null) MessageBox.Show(ex.Message);
        }
    }

    [RelayCommand]
    private async Task DecreaseQuantity(CartItemViewModel? vm)
    {
        if (CurrentSale == null || vm == null) return;
        try
        {
            decimal newQty = Math.Round(vm.Model.Quantity - vm.StepAmount, 3, MidpointRounding.AwayFromZero);
            if (newQty <= 0m)
            {
                await RemoveItem(vm);
                return;
            }

            int selectedId = vm.Id;
            CurrentSale = await _salesService.UpdateItemQuantityAsync(CurrentSale.Id, vm.Id, newQty, _exchangeRateService.CurrentRate);
            SelectedSaleItem = CartItems.FirstOrDefault(i => i.Id == selectedId);
        }
        catch (System.Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowWarning("Error", ex.Message);
            else if (Application.Current != null) MessageBox.Show(ex.Message);
        }
    }

    [RelayCommand]
    private async Task RemoveItem(CartItemViewModel? vm)
    {
        if (CurrentSale == null || vm == null) return;
        try
        {
            CurrentSale = await _salesService.RemoveItemAsync(CurrentSale.Id, vm.Id, _exchangeRateService.CurrentRate);
        }
        catch (System.Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Error", $"Error removing item: {ex.Message}");
            else if (Application.Current != null) MessageBox.Show($"Error removing item: {ex.Message}");
        }
    }

    public async Task CommitItemQuantityAsync(int itemId, decimal newQty)
    {
        if (CurrentSale == null) return;
        try
        {
            if (newQty <= 0m)
            {
                var itemToRemove = CartItems.FirstOrDefault(i => i.Id == itemId);
                if (itemToRemove != null)
                {
                    await RemoveItem(itemToRemove);
                }
                return;
            }

            var updated = await _salesService.UpdateItemQuantityAsync(CurrentSale.Id, itemId, newQty, _exchangeRateService.CurrentRate);
            
            var existingVm = CartItems.FirstOrDefault(i => i.Id == itemId);
            if (existingVm != null)
            {
                var updatedItem = updated.Items.FirstOrDefault(i => i.Id == itemId);
                if (updatedItem != null)
                {
                    existingVm.Model.Quantity = updatedItem.Quantity;
                    existingVm.Model.Subtotal = updatedItem.Subtotal;
                    existingVm.Model.UnitPriceBsS = updatedItem.UnitPriceBsS;
                    existingVm.Model.SubtotalBsS = updatedItem.SubtotalBsS;
                    existingVm.NotifyRecalculation();
                }
            }

            _currentSale = updated;
            RecalculateTotals();
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CartViewModel] CommitItemQuantityAsync error: {ex.Message}");
        }
    }

    public async Task FlushAllQuantitiesAsync()
    {
        if (CurrentSale == null) return;
        try
        {
            foreach (var item in CartItems.ToList())
            {
                if (item.Model.Quantity > 0m)
                {
                    await _salesService.UpdateItemQuantityAsync(CurrentSale.Id, item.Id, item.Model.Quantity, _exchangeRateService.CurrentRate);
                }
            }
            var reloaded = await _salesService.GetSaleAsync(CurrentSale.Id);
            CurrentSale = reloaded;
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CartViewModel] FlushAllQuantitiesAsync error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
