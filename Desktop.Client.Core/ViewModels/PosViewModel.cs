using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Services;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Orchestrates the POS UI, including product search and coordination with the Cart logic.
/// </summary>
public partial class PosViewModel : ObservableObject
{
    private readonly ISalesService _sales_service;
    private readonly IProductService _product_service;
    private readonly IPaymentService _payment_service;
    private readonly IExchangeRateService _exchange_rate_service;

    private CartViewModel _cart;
    public CartViewModel Cart
    {
        get => _cart;
        set => SetProperty(ref _cart, value);
    }

    private bool _is_processing;
    public bool IsProcessing
    {
        get => _is_processing;
        set => SetProperty(ref _is_processing, value);
    }

    public decimal CurrentExchangeRate => _exchange_rate_service.CurrentRate;

    public ObservableCollection<PaymentMethodDto> ActivePaymentMethods { get; } = new();

    private System.Threading.CancellationTokenSource? _cancellation_token_source;

    private ObservableCollection<Core.DTOs.ProductQuickInfoDto> _suggestions = new();
    public ObservableCollection<Core.DTOs.ProductQuickInfoDto> Suggestions
    {
        get => _suggestions;
        private set => SetProperty(ref _suggestions, value);
    }

    private string _search_text = string.Empty;
    public string SearchText
    {
        get => _search_text;
        set
        {
            if (SetProperty(ref _search_text, value))
            {
                _ = ExecuteSearchAsync();
            }
        }
    }

    private bool _has_suggestions;
    public bool HasSuggestions
    {
        get => _has_suggestions;
        set => SetProperty(ref _has_suggestions, value);
    }

    private bool _is_searching;
    public bool IsSearching
    {
        get => _is_searching;
        set => SetProperty(ref _is_searching, value);
    }

    private Core.DTOs.ProductQuickInfoDto? _selected_suggestion;
    public Core.DTOs.ProductQuickInfoDto? SelectedSuggestion
    {
        get => _selected_suggestion;
        set => SetProperty(ref _selected_suggestion, value);
    }

    private readonly UserSession? _user_session;
    private readonly IDialogService? _dialog_service;

    public PosViewModel(
        ISalesService sales_service, 
        IProductService product_service, 
        IPaymentService payment_service, 
        IExchangeRateService exchange_rate_service,
        CartViewModel cart_view_model,
        UserSession? user_session = null,
        IDialogService? dialog_service = null)
    {
        _sales_service = sales_service;
        _product_service = product_service;
        _payment_service = payment_service;
        _exchange_rate_service = exchange_rate_service;
        _cart = cart_view_model;
        _user_session = user_session;
        _dialog_service = dialog_service;

        // Sync local property when exchange rate changes globaly
        WeakReferenceMessenger.Default.Register<Desktop.Client.Messages.ExchangeRateChangedMessage>(this, (r, m) =>
        {
            OnPropertyChanged(nameof(CurrentExchangeRate));
        });

        WeakReferenceMessenger.Default.Register<Desktop.Client.ViewModels.PaymentMethodsChangedMessage>(this, async (r, m) =>
        {
            await ((PosViewModel)r).ReloadPaymentMethodsAsync();
        });

        _ = LoadPaymentMethodsAsync();
        _ = StartNewSaleAsync();
    }

    private async Task LoadPaymentMethodsAsync()
    {
        int _max_retries = 3;
        int _delay_ms = 2000;

        for (int _attempt = 1; _attempt <= _max_retries; _attempt++)
        {
            try
            {
                var _methods = await _payment_service.GetActiveMethodsAsync();
                if (!_methods.Any())
                {
                    MessageBox.Show("CRITICAL: There are no active payment methods configured in the system. Sales cannot be processed until the administrator adds at least one payment configuration in Settings.", "System Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                foreach (var _m in _methods)
                {
                    ActivePaymentMethods.Add(_m);
                }
                return;
            }
            catch (System.Exception _ex)
            {
                if (_attempt == _max_retries)
                {
                    MessageBox.Show($"Failed to load payment configurations after {_max_retries} attempts: {_ex.Message}");
                }
                else
                {
                    await Task.Delay(_delay_ms);
                }
            }
        }
    }

    private async Task ReloadPaymentMethodsAsync()
    {
        _payment_service.InvalidateCache();
        ActivePaymentMethods.Clear();
        await LoadPaymentMethodsAsync();
    }

    private async Task StartNewSaleAsync()
    {
        System.Diagnostics.Debug.WriteLine("[POS] StartNewSaleAsync: calling StartSaleAsync...");
        IsProcessing = true;
        try
        {
            var _newSale = await _sales_service.StartSaleAsync(_user_session?.CurrentUser?.Id);
            System.Diagnostics.Debug.WriteLine($"[POS] StartNewSaleAsync: sale started OK, Id={_newSale.Id}");
            Cart.CurrentSale = _newSale;
            System.Diagnostics.Debug.WriteLine($"[POS] StartNewSaleAsync: Cart.CurrentSale set. IsNull={Cart.CurrentSale == null}");
        }
        catch (System.Exception _ex)
        {
            System.Diagnostics.Debug.WriteLine($"[POS] StartNewSaleAsync FAILED: {_ex.GetType().Name}: {_ex.Message}");
            MessageBox.Show($"Error starting sale: {_ex.Message}", "Sale Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task ExecuteSearchAsync()
    {
        _cancellation_token_source?.Cancel();
        _cancellation_token_source = new System.Threading.CancellationTokenSource();
        var _token = _cancellation_token_source.Token;

        var _term = SearchText ?? string.Empty;
        var _dispatcher = System.Windows.Application.Current.Dispatcher;

        void RunOnUI(Action action)
        {
            if (_dispatcher.CheckAccess()) action();
            else _dispatcher.Invoke(action);
        }

        if (string.IsNullOrWhiteSpace(_term))
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
            IsSearching = true;
            var _results = await _product_service.GetSuggestionsAsync(_term, true, _token);

            RunOnUI(() =>
            {
                Suggestions.Clear();

                if (!_results.Any())
                {
                    Suggestions.Add(new Core.DTOs.ProductQuickInfoDto { Id = -1, Name = "Product not found", SKU = "-" });
                }
                else
                {
                    foreach (var _item in _results)
                    {
                        _item.PriceBsS = Helpers.PricingHelper.ToBsS(_item.PriceUSD, CurrentExchangeRate);
                        Suggestions.Add(_item);
                    }
                }

                HasSuggestions = Suggestions.Any();
                if (SelectedSuggestion != null) SelectedSuggestion = null;
            });
        }
        catch (System.OperationCanceledException) { }
        catch (System.Exception)
        {
            RunOnUI(() =>
            {
                Suggestions.Clear();
                HasSuggestions = false;
            });
        }
        finally
        {
            if (!_token.IsCancellationRequested)
            {
                RunOnUI(() => IsSearching = false);
            }
        }
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
        if (_dialog_service == null) return;

        var customer = await _dialog_service.ShowCustomerPickerAsync();
        if (customer == null) return;

        try
        {
            var updatedSale = await _sales_service.UpdateSaleCustomerAsync(Cart.CurrentSale.Id, customer.Id);
            Cart.CurrentSale = updatedSale;
        }
        catch (System.Exception ex)
        {
            _dialog_service.ShowError("Error al cambiar cliente", $"No se pudo actualizar el cliente de la venta: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AddSelectedSuggestionAsync(Core.DTOs.ProductQuickInfoDto? suggestion)
    {
        var _value = suggestion ?? SelectedSuggestion;
        System.Diagnostics.Debug.WriteLine($"[POS] AddSelectedSuggestionAsync called. resolved='{_value?.Name ?? "null"}' (Id={_value?.Id ?? -99}), Cart.CurrentSale null={Cart.CurrentSale == null}");

        if (_value == null || _value.Id <= 0)
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
                System.Windows.MessageBox.Show("Could not start a sale session. Please check that the server is running and try again.", "Connection Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            System.Diagnostics.Debug.WriteLine("[POS] Lazy start succeeded.");
        }

        if (_value != null && _value.Id > 0 && Cart.CurrentSale != null)
        {
            decimal? _custom_price_usd = null;
            decimal? _custom_price_local = null;

            if (_value.IsCashAdvance)
            {
                if (CurrentExchangeRate <= 0)
                {
                    System.Windows.MessageBox.Show("Please set a valid Exchange Rate in the top header before requesting a cash advance.", "Missing Rate", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    SelectedSuggestion = null;
                    return;
                }

                decimal? requestedBsS = _dialog_service?.ShowCashAdvanceDialog();

                if (requestedBsS.HasValue && requestedBsS.Value > 0)
                {
                    decimal _total_bs_s = requestedBsS.Value * (1 + (_value.ProfitPercentage / 100m));
                    _custom_price_usd = _total_bs_s / CurrentExchangeRate;
                    _custom_price_local = _total_bs_s;
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
                Cart.CurrentSale = await _sales_service.AddItemAsync(Cart.CurrentSale.Id, _value.Id, 1, CurrentExchangeRate, _custom_price_usd, _custom_price_local);
            }
            catch (System.Exception _ex)
            {
                MessageBox.Show($"Error adding item: {_ex.Message}");
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

        if (!Cart.CartItems.Any())
        {
            MessageBox.Show("Cart is empty. Please add items before checking out.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (CurrentExchangeRate <= 0)
        {
            MessageBox.Show("Cannot proceed to checkout. Please set a valid Exchange Rate in the top header.", "Missing Rate", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var _checkout_vm = new CheckoutViewModel(Cart.CurrentSale, ActivePaymentMethods, _sales_service, CurrentExchangeRate, _user_session);
        var _result = await MaterialDesignThemes.Wpf.DialogHost.Show(_checkout_vm, "RootDialog");

        if (_result is int _real_invoice)
        {
            string formattedMessage = _checkout_vm.IsPendingPickup
                ? $"Factura N° {_real_invoice:D5}: Cuenta liquidada, stock descontado y enviada a Mercancía en Custodia."
                : $"¡Factura N° {_real_invoice:D5} completada con éxito!";

            _dialog_service?.ShowSuccessDialog(formattedMessage);
            
            _ = StartNewSaleAsync();
        }
    }

    [RelayCommand]
    private async Task HoldOrderAsync()
    {
        if (IsProcessing) return;
        if (Cart.CurrentSale == null) return;

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
            if (_dialog_service == null) return;

            MessageBox.Show(
                "Las ventas en espera requieren asignar un cliente real registrado.\nA continuación seleccione o registre un cliente.",
                "Cliente Requerido", MessageBoxButton.OK, MessageBoxImage.Information);

            var selectedCustomer = await _dialog_service.ShowCustomerPickerAsync();
            if (selectedCustomer == null || selectedCustomer.IsDefault || selectedCustomer.CedulaOrRif == "V-00000000")
            {
                MessageBox.Show(
                    "Operación cancelada. No se puede guardar en espera a nombre del Consumidor Final.",
                    "Cliente Inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Cart.CurrentSale = await _sales_service.UpdateSaleCustomerAsync(Cart.CurrentSale.Id, selectedCustomer.Id);
            }
            catch (System.Exception ex)
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

            var heldSale = await _sales_service.HoldSaleAsync(Cart.CurrentSale.Id, request);

            string customerName = heldSale.CustomerName ?? Cart.CurrentSale.CustomerName ?? "Cliente";
            string successMsg = $"¡Pedido #{heldSale.Id} guardado exitosamente en Cuentas Abiertas para {customerName}!";

            _dialog_service?.ShowSuccessDialog(successMsg);

            await StartNewSaleAsync();
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Error al guardar pedido en espera: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }
}

