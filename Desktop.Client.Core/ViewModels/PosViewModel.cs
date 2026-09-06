using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Orchestrates the POS UI, including product search, sales workflow, and coordination with the Cart logic.
/// </summary>
public partial class PosViewModel : ObservableObject, IDisposable
{
    private readonly ISalesService _salesService;
    private readonly IProductService _productService;
    private readonly IPaymentService _paymentService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly UserSession? _userSession;
    private readonly IDialogService? _dialogService;

    private CartViewModel _cart;
    public CartViewModel Cart
    {
        get => _cart;
        set => SetProperty(ref _cart, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    public decimal CurrentExchangeRate => _exchangeRateService.CurrentRate;
    public bool IsRateOutdated => _exchangeRateService.IsRateOutdated;

    public ObservableCollection<PaymentMethodDto> ActivePaymentMethods { get; } = new();

    private CancellationTokenSource? _cancellationTokenSource;

    private ObservableCollection<ProductQuickInfoDto> _suggestions = new();
    public ObservableCollection<ProductQuickInfoDto> Suggestions
    {
        get => _suggestions;
        private set => SetProperty(ref _suggestions, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _ = ExecuteSearchAsync();
            }
        }
    }

    private bool _hasSuggestions;
    public bool HasSuggestions
    {
        get => _hasSuggestions;
        set => SetProperty(ref _hasSuggestions, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }

    private ProductQuickInfoDto? _selectedSuggestion;
    public ProductQuickInfoDto? SelectedSuggestion
    {
        get => _selectedSuggestion;
        set => SetProperty(ref _selectedSuggestion, value);
    }

    public PosViewModel(
        ISalesService salesService, 
        IProductService productService, 
        IPaymentService paymentService, 
        IExchangeRateService exchangeRateService,
        CartViewModel cartViewModel,
        UserSession? userSession = null,
        IDialogService? dialogService = null,
        ISalesService? sales_service = null,
        IProductService? product_service = null,
        IPaymentService? payment_service = null,
        IExchangeRateService? exchange_rate_service = null,
        CartViewModel? cart_view_model = null,
        UserSession? user_session = null,
        IDialogService? dialog_service = null)
    {
        _salesService = salesService ?? sales_service ?? throw new ArgumentNullException(nameof(salesService));
        _productService = productService ?? product_service ?? throw new ArgumentNullException(nameof(productService));
        _paymentService = paymentService ?? payment_service ?? throw new ArgumentNullException(nameof(paymentService));
        _exchangeRateService = exchangeRateService ?? exchange_rate_service ?? throw new ArgumentNullException(nameof(exchangeRateService));
        _cart = cartViewModel ?? cart_view_model ?? throw new ArgumentNullException(nameof(cartViewModel));
        _userSession = userSession ?? user_session;
        _dialogService = dialogService ?? dialog_service;

        // Sync local property when exchange rate changes globally
        WeakReferenceMessenger.Default.Register<ExchangeRateChangedMessage>(this, (r, m) =>
        {
            OnPropertyChanged(nameof(CurrentExchangeRate));
            OnPropertyChanged(nameof(IsRateOutdated));
        });

        WeakReferenceMessenger.Default.Register<PaymentMethodsChangedMessage>(this, async (r, m) =>
        {
            await ((PosViewModel)r).ReloadPaymentMethodsAsync();
        });
    }

    private readonly SemaphoreSlim _sessionInitializationGate = new(1, 1);

    public void ResetSession()
    {
        Action clearAction = () =>
        {
            ActivePaymentMethods.Clear();
            Cart.CurrentSale = null;
            Cart.CartItems.Clear();
            RecentScannedProducts.Clear();
            SearchText = string.Empty;
            Suggestions.Clear();
        };

        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(clearAction);
        }
        else
        {
            clearAction();
        }
    }

    public async Task InitializeForSessionAsync()
    {
        if (_userSession != null && !_userSession.IsLoggedIn) return;

        await _sessionInitializationGate.WaitAsync();
        try
        {
            if (_userSession != null && !_userSession.IsLoggedIn) return;

            if (CurrentExchangeRate <= 0)
            {
                await _exchangeRateService.GetCurrentRateAsync();
                OnPropertyChanged(nameof(CurrentExchangeRate));
                OnPropertyChanged(nameof(IsRateOutdated));
            }

            if (ActivePaymentMethods.Count == 0)
            {
                await LoadPaymentMethodsAsync();
            }

            if (Cart.CurrentSale == null)
            {
                await StartNewSaleAsync();
            }
        }
        finally
        {
            _sessionInitializationGate.Release();
        }
    }

    private async Task LoadPaymentMethodsAsync()
    {
        if (_userSession != null && !_userSession.IsLoggedIn) return;

        int maxRetries = 3;
        int delayMs = 2000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var methods = (await _paymentService.GetActiveMethodsAsync())?.ToList() ?? new List<PaymentMethodDto>();
                if (!methods.Any())
                {
                    _dialogService?.ShowError("Error de Configuración", "No hay métodos de pago activos configurados en el sistema. Las ventas no podrán procesarse hasta que el administrador agregue al menos una configuración.");
                    return;
                }

                Action updateAction = () =>
                {
                    ActivePaymentMethods.Clear();
                    foreach (var m in methods)
                    {
                        if (!ActivePaymentMethods.Any(existing => existing.Id == m.Id))
                        {
                            ActivePaymentMethods.Add(m);
                        }
                    }
                };

                if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.Invoke(updateAction);
                }
                else
                {
                    updateAction();
                }
                return;
            }
            catch (Exception ex)
            {
                if (_userSession != null && !_userSession.IsLoggedIn) return;

                if (attempt == maxRetries)
                {
                    _dialogService?.ShowError("Error de Conexión", $"Error al cargar métodos de pago tras {maxRetries} intentos: {ex.Message}");
                }
                else
                {
                    await Task.Delay(delayMs);
                }
            }
        }
    }

    public async Task ReloadPaymentMethodsAsync()
    {
        _paymentService.InvalidateCache();
        await _sessionInitializationGate.WaitAsync();
        try
        {
            await LoadPaymentMethodsAsync();
        }
        finally
        {
            _sessionInitializationGate.Release();
        }
    }

    private async Task StartNewSaleAsync()
    {
        if (_userSession != null && !_userSession.IsLoggedIn) return;

        System.Diagnostics.Debug.WriteLine("[POS] StartNewSaleAsync: calling StartSaleAsync...");
        IsProcessing = true;
        try
        {
            var newSale = await _salesService.StartSaleAsync(_userSession?.CurrentUser?.Id);
            System.Diagnostics.Debug.WriteLine($"[POS] StartNewSaleAsync: sale started OK, Id={newSale.Id}");
            Cart.CurrentSale = newSale;
            RecentScannedProducts.Clear();
            System.Diagnostics.Debug.WriteLine($"[POS] StartNewSaleAsync: Cart.CurrentSale set. IsNull={Cart.CurrentSale == null}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[POS] StartNewSaleAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show($"Error starting sale: {ex.Message}", "Sale Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task ExecuteSearchAsync()
    {
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _cancellationTokenSource, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var token = newCts.Token;
        var term = SearchText ?? string.Empty;
        var dispatcher = Application.Current?.Dispatcher;

        void RunOnUI(Action action)
        {
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        if (string.IsNullOrWhiteSpace(term))
        {
            RunOnUI(() =>
            {
                Suggestions.Clear();
                HasSuggestions = false;
            });
            return;
        }

        try
        {
            // 300ms debounce to prevent overwhelming the server during fast typing
            await Task.Delay(300, token);

            IsSearching = true;
            var results = await _productService.GetSuggestionsAsync(term, true, token);

            RunOnUI(() =>
            {
                Suggestions.Clear();

                if (!results.Any())
                {
                    Suggestions.Add(new ProductQuickInfoDto { Id = -1, Name = "Product not found", SKU = "-" });
                }
                else
                {
                    foreach (var item in results)
                    {
                        item.PriceBsS = Helpers.PricingHelper.ToBsS(item.PriceUSD, CurrentExchangeRate);
                        Suggestions.Add(item);
                    }
                }

                HasSuggestions = Suggestions.Any();
                if (SelectedSuggestion != null) SelectedSuggestion = null;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            RunOnUI(() =>
            {
                Suggestions.Clear();
                HasSuggestions = false;
            });
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                RunOnUI(() => IsSearching = false);
            }
        }
    }

    public void Dispose()
    {
        var oldCts = Interlocked.Exchange(ref _cancellationTokenSource, null);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _scannerLock.Dispose();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _sessionInitializationGate.Dispose();
        }
        catch (ObjectDisposedException) { }

        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    [RelayCommand]
    private async Task SearchGotFocusAsync()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            await ExecuteSearchAsync();
        }
    }

    [RelayCommand]
    private async Task ChangeCustomerAsync()
    {
        if (Cart.CurrentSale == null) return;
        if (_dialogService == null) return;

        var customer = await _dialogService.ShowCustomerPickerAsync();
        if (customer == null) return;

        try
        {
            var updatedSale = await _salesService.UpdateSaleCustomerAsync(Cart.CurrentSale.Id, customer.Id);
            Cart.CurrentSale = updatedSale;
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error al cambiar cliente", $"No se pudo actualizar el cliente de la venta: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AddSelectedSuggestionAsync(ProductQuickInfoDto? suggestion)
    {
        var value = suggestion ?? SelectedSuggestion;
        System.Diagnostics.Debug.WriteLine($"[POS] AddSelectedSuggestionAsync called. resolved='{value?.Name ?? "null"}' (Id={value?.Id ?? -99}), Cart.CurrentSale null={Cart.CurrentSale == null}");

        if (value == null || value.Id <= 0)
        {
            System.Diagnostics.Debug.WriteLine("[POS] Guard: value is null or Id <= 0, ignoring.");
            return;
        }

        // Lazy-start: if the sale hasn't been created yet (e.g. startup race), try now
        if (Cart.CurrentSale == null)
        {
            System.Diagnostics.Debug.WriteLine("[POS] Cart.CurrentSale is NULL — attempting lazy StartSaleAsync...");
            await StartNewSaleAsync();

            // If still null after the attempt, the API is unreachable — abort with visible error
            if (Cart.CurrentSale == null)
            {
                System.Diagnostics.Debug.WriteLine("[POS] Lazy start FAILED — CurrentSale still null after retry.");
                MessageBox.Show("Could not start a sale session. Please check that the server is running and try again.", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            System.Diagnostics.Debug.WriteLine("[POS] Lazy start succeeded.");
        }

        if (value != null && value.Id > 0 && Cart.CurrentSale != null)
        {
            if (value.IsGroupHeader)
            {
                var variant = await (_dialogService?.ShowVariantSelectionDialogAsync(value) ?? Task.FromResult<ProductDto?>(null));
                if (variant == null)
                {
                    SelectedSuggestion = null;
                    return;
                }

                value = new ProductQuickInfoDto
                {
                    Id = variant.Id,
                    Name = variant.Name,
                    SKU = variant.SKU,
                    PriceUSD = variant.PriceUSD,
                    PriceRetailUSD = variant.PriceRetailUSD,
                    PriceWholesaleUSD = variant.PriceWholesaleUSD,
                    PriceBsS = variant.PriceBsS,
                    StockQuantity = variant.StockQuantity,
                    IsActive = variant.IsActive
                };
            }

            decimal? customPriceUsd = null;
            decimal? customPriceLocal = null;

            if (value.IsCashAdvance)
            {
                if (CurrentExchangeRate <= 0)
                {
                    MessageBox.Show("Please set a valid Exchange Rate in the top header before requesting a cash advance.", "Missing Rate", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SelectedSuggestion = null;
                    return;
                }

                decimal? requestedBsS = _dialogService?.ShowCashAdvanceDialog();

                if (requestedBsS.HasValue && requestedBsS.Value > 0)
                {
                    decimal totalBsS = requestedBsS.Value * (1 + (value.ProfitPercentage / 100m));
                    customPriceUsd = totalBsS / CurrentExchangeRate;
                    customPriceLocal = totalBsS;
                }
                else
                {
                    SelectedSuggestion = null;
                    return;
                }
            }

            IsProcessing = true;
            try
            {
                Cart.CurrentSale = await _salesService.AddItemAsync(Cart.CurrentSale.Id, value.Id, 1, CurrentExchangeRate, customPriceUsd, customPriceLocal);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error adding item: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                SearchText = string.Empty;
                SelectedSuggestion = null;
                Suggestions.Clear();
                HasSuggestions = false;
            }
        }
    }

    [RelayCommand]
    private async Task CheckoutAsync()
    {
        if (IsProcessing) return;
        if (Cart.CurrentSale == null) return;

        await Cart.FlushAllQuantitiesAsync();

        if (!Cart.CartItems.Any())
        {
            if (_dialogService != null)
                _dialogService.ShowWarning("Validación", "El carrito está vacío. Por favor agregue productos antes de cobrar.");
            else
                MessageBox.Show("El carrito está vacío. Por favor agregue productos antes de cobrar.", "Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (CurrentExchangeRate <= 0)
        {
            if (_dialogService != null)
                _dialogService.ShowWarning("Tasa Requerida", "No se puede proceder al cobro. Por favor establezca una tasa de cambio válida en el encabezado.");
            else
                MessageBox.Show("No se puede proceder al cobro. Por favor establezca una tasa de cambio válida en el encabezado.", "Tasa Requerida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var checkoutVm = new CheckoutViewModel(Cart.CurrentSale, ActivePaymentMethods, _salesService, CurrentExchangeRate, _userSession, override_sale: null, dialog_service: _dialogService);
        var result = await MaterialDesignThemes.Wpf.DialogHost.Show(checkoutVm, "RootDialog");

        if (result is int realInvoice)
        {
            string formattedMessage = checkoutVm.IsPendingPickup
                ? $"Factura N° {realInvoice:D5}: Cuenta liquidada, stock descontado y enviada a Mercancía en Custodia."
                : $"¡Factura N° {realInvoice:D5} completada con éxito!";

            _dialogService?.ShowSuccessDialog(formattedMessage);
            
            _ = StartNewSaleAsync();
        }
    }

    [RelayCommand]
    private async Task HoldOrderAsync()
    {
        if (IsProcessing) return;
        if (Cart.CurrentSale == null) return;

        await Cart.FlushAllQuantitiesAsync();

        if (!Cart.CartItems.Any())
        {
            MessageBox.Show("El carrito está vacío. Agregue productos antes de guardar en espera.", "Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (CurrentExchangeRate <= 0)
        {
            MessageBox.Show("No se puede guardar en espera. Por favor establezca una tasa de cambio válida.", "Tasa Requerida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Customer validation: Hold sale requires a registered real customer (cannot be Consumidor Final / IsDefault / V-00000000)
        var currentCustomer = Cart.CurrentSale.Customer;
        bool isDefaultCustomer = currentCustomer == null || currentCustomer.IsDefault || currentCustomer.CedulaOrRif == "V-00000000";

        if (isDefaultCustomer)
        {
            if (_dialogService == null) return;

            MessageBox.Show(
                "Las ventas en espera requieren asignar un cliente real registrado.\nA continuación seleccione o registre un cliente.",
                "Cliente Requerido", MessageBoxButton.OK, MessageBoxImage.Information);

            var selectedCustomer = await _dialogService.ShowCustomerPickerAsync();
            if (selectedCustomer == null || selectedCustomer.IsDefault || selectedCustomer.CedulaOrRif == "V-00000000")
            {
                MessageBox.Show(
                    "Operación cancelada. No se puede guardar en espera a nombre del Consumidor Final.",
                    "Cliente Inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Cart.CurrentSale = await _salesService.UpdateSaleCustomerAsync(Cart.CurrentSale.Id, selectedCustomer.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al asignar cliente: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        if (!Cart.CurrentSale.CustomerId.HasValue)
        {
            MessageBox.Show("Error de consistencia: La venta no posee cliente asociado.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        IsProcessing = true;
        try
        {
            var request = new HoldSaleRequestDto
            {
                CustomerId = Cart.CurrentSale.CustomerId.Value,
                ExchangeRate = CurrentExchangeRate,
                IsProductDelivered = false,
                InitialPayments = null
            };

            var heldSale = await _salesService.HoldSaleAsync(Cart.CurrentSale.Id, request);

            string customerName = heldSale.CustomerName ?? Cart.CurrentSale.CustomerName ?? "Cliente";
            string successMsg = $"¡Pedido #{heldSale.Id} guardado exitosamente en Cuentas Abiertas para {customerName}!";

            _dialogService?.ShowSuccessDialog(successMsg);

            await StartNewSaleAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al guardar pedido en espera: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task TogglePriceListAsync()
    {
        if (Cart.CurrentSale == null || IsProcessing) return;
        string nextType = Cart.IsWholesalePriceList ? "Retail" : "Wholesale";
        await Cart.SetPriceListAsync(nextType);
    }

    [RelayCommand]
    private async Task ClearCartAsync()
    {
        if (Cart.CurrentSale == null || !Cart.CartItems.Any() || IsProcessing) return;

        bool confirmed = _dialogService != null
            ? _dialogService.ShowConfirm("Cancelar Venta (F8)", "¿Está seguro de que desea cancelar la venta actual y limpiar el carrito?")
            : MessageBox.Show("¿Está seguro de que desea cancelar la venta actual y limpiar el carrito?", "Cancelar Venta (F8)", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        if (confirmed)
        {
            await StartNewSaleAsync();
        }
    }

    [RelayCommand]
    private void CancelOrClear()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            SearchText = string.Empty;
            Suggestions.Clear();
            HasSuggestions = false;
        }
    }

    [RelayCommand]
    private async Task SyncExchangeRateAsync()
    {
        try
        {
            await _exchangeRateService.SyncBcvAsync();
            OnPropertyChanged(nameof(CurrentExchangeRate));
            OnPropertyChanged(nameof(IsRateOutdated));
        }
        catch { }
    }
}
