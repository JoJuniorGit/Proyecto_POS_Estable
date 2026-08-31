using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
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
    private readonly ISalesService _sales_service;
    private readonly IExchangeRateService _exchange_rate_service;

    public CartViewModel(ISalesService sales_service, IExchangeRateService exchange_rate_service)
    {
        _sales_service = sales_service;
        _exchange_rate_service = exchange_rate_service;

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
                    var updated = await _sales_service.GetSaleAsync(CurrentSale.Id);
                    CurrentSale = updated;
                }
                catch
                {
                    // Ignore transient network errors during background refresh
                }
            }
        });
    }

    private ObservableCollection<CartItemViewModel> _cart_items = new();
    public ObservableCollection<CartItemViewModel> CartItems
    {
        get => _cart_items;
        private set => SetProperty(ref _cart_items, value);
    }

    private CartItemViewModel? _selected_sale_item;
    public CartItemViewModel? SelectedSaleItem
    {
        get => _selected_sale_item;
        set => SetProperty(ref _selected_sale_item, value);
    }

    private decimal _total_usd;
    public decimal TotalUSD
    {
        get => _total_usd;
        set => SetProperty(ref _total_usd, value);
    }

    private decimal _subtotal;
    public decimal Subtotal
    {
        get => _subtotal;
        set => SetProperty(ref _subtotal, value);
    }

    private bool _is_empty = true;
    public bool IsEmpty
    {
        get => _is_empty;
        set => SetProperty(ref _is_empty, value);
    }

    public decimal SubtotalLocal => CurrentSale != null && CurrentSale.Status != "Pending"
        ? CurrentSale.SubtotalBsS 
        : PricingHelper.ToBsS(Subtotal, _exchange_rate_service.CurrentRate);
        
    public decimal TotalAmountLocal => CurrentSale != null && CurrentSale.Status != "Pending"
        ? CurrentSale.TotalBsS 
        : PricingHelper.ToBsS(TotalUSD, _exchange_rate_service.CurrentRate);

    private SaleDto? _current_sale;
    public SaleDto? CurrentSale
    {
        get => _current_sale;
        set
        {
            if (SetProperty(ref _current_sale, value))
            {
                OnPropertyChanged(nameof(CustomerName));
                OnPropertyChanged(nameof(CustomerCedula));
                OnPropertyChanged(nameof(PriceListType));
                OnPropertyChanged(nameof(IsWholesalePriceList));
                UpdateCollection();
            }
        }
    }

    public string CustomerName => _current_sale?.CustomerName ?? "Consumidor Final";
    public string CustomerCedula => _current_sale?.CustomerCedula ?? "V-00000000";
    public string PriceListType => _current_sale?.PriceListType ?? "Retail";
    public bool IsWholesalePriceList => PriceListType == "Wholesale";

    [RelayCommand]
    public async Task SetPriceListAsync(string type)
    {
        if (CurrentSale == null || string.Equals(CurrentSale.PriceListType, type, System.StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var updated = await _sales_service.UpdatePriceListAsync(CurrentSale.Id, type);
            CurrentSale = updated;
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(ex.Message, "Lista de Precios", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                decimal rateToUse = CurrentSale.AppliedRate > 0 ? CurrentSale.AppliedRate : _exchange_rate_service.CurrentRate;
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

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            DoUpdate();
        else
            dispatcher.Invoke(DoUpdate);
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
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var updated = await _sales_service.GetSaleAsync(CurrentSale.Id);
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null && !dispatcher.CheckAccess())
                        {
                            dispatcher.Invoke(() => CurrentSale = updated);
                        }
                        else
                        {
                            CurrentSale = updated;
                        }
                    }
                    catch
                    {
                        // Ignore transient network issues
                    }
                });
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

    [RelayCommand]
    private async Task IncreaseQuantity(CartItemViewModel? vm)
    {
        if (CurrentSale == null || vm == null) return;
        try
        {
            int selectedId = vm.Id;
            decimal newQty = Math.Round(vm.Model.Quantity + vm.StepAmount, 3, MidpointRounding.AwayFromZero);
            CurrentSale = await _sales_service.UpdateItemQuantityAsync(CurrentSale.Id, vm.Id, newQty, _exchange_rate_service.CurrentRate);
            SelectedSaleItem = CartItems.FirstOrDefault(i => i.Id == selectedId);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(ex.Message);
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
            CurrentSale = await _sales_service.UpdateItemQuantityAsync(CurrentSale.Id, vm.Id, newQty, _exchange_rate_service.CurrentRate);
            SelectedSaleItem = CartItems.FirstOrDefault(i => i.Id == selectedId);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(ex.Message);
        }
    }

    [RelayCommand]
    private async Task RemoveItem(CartItemViewModel? vm)
    {
        if (CurrentSale == null || vm == null) return;
        try
        {
            CurrentSale = await _sales_service.RemoveItemAsync(CurrentSale.Id, vm.Id, _exchange_rate_service.CurrentRate);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Error removing item: {ex.Message}");
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

            var updated = await _sales_service.UpdateItemQuantityAsync(CurrentSale.Id, itemId, newQty, _exchange_rate_service.CurrentRate);
            
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

            _current_sale = updated;
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
                    await _sales_service.UpdateItemQuantityAsync(CurrentSale.Id, item.Id, item.Model.Quantity, _exchange_rate_service.CurrentRate);
                }
            }
            var reloaded = await _sales_service.GetSaleAsync(CurrentSale.Id);
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
