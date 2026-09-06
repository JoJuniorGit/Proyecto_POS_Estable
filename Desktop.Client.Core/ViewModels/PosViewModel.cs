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
        IDialogService? dialogService = null)
    {
        _salesService = salesService ?? throw new ArgumentNullException(nameof(salesService));
        _productService = productService ?? throw new ArgumentNullException(nameof(productService));
        _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
        _exchangeRateService = exchangeRateService ?? throw new ArgumentNullException(nameof(exchangeRateService));
        _cart = cartViewModel ?? throw new ArgumentNullException(nameof(cartViewModel));
        _userSession = userSession;
        _dialogService = dialogService;

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
}
