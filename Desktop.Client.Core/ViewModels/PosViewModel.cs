using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.Common;
using Core.DTOs;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
    private readonly IDispatcherInvoker _dispatcherInvoker;
    private readonly ISaleRecoveryStore? _recoveryStore;
    private readonly ISettingsService? _settingsService;
    private bool _recoveryCheckCompleted;

    // 8.185-T4 (S1/S2): regla cash global y toggle de piso vigentes para el Bs.S efectivo de las
    // sugerencias. Una lectura al iniciar y refresco por mensaje: 0 consultas por búsqueda.
    private RoundingRuleType? _globalCashRoundingRule;
    private bool _neverRoundDownCashBs = true;

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
                ExecuteSearchAsync().SafeFireAndForget("PosViewModel.ExecuteSearch");
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
        set
        {
            if (SetProperty(ref _selectedSuggestion, value))
            {
                OnSelectedSuggestionChanged(value);
            }
        }
    }

    partial void OnSelectedSuggestionChanged(ProductQuickInfoDto? value);

    public PosViewModel(
        ISalesService salesService, 
        IProductService productService, 
        IPaymentService paymentService, 
        IExchangeRateService exchangeRateService,
        CartViewModel cartViewModel,
        UserSession? userSession = null,
        IDialogService? dialogService = null,
        IDispatcherInvoker? dispatcherInvoker = null,
        ISaleRecoveryStore? recoveryStore = null,
        ISettingsService? settingsService = null)
    {
        _salesService = salesService ?? throw new ArgumentNullException(nameof(salesService));
        _productService = productService ?? throw new ArgumentNullException(nameof(productService));
        _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
        _exchangeRateService = exchangeRateService ?? throw new ArgumentNullException(nameof(exchangeRateService));
        _cart = cartViewModel ?? throw new ArgumentNullException(nameof(cartViewModel));
        _userSession = userSession;
        _dialogService = dialogService;
        _dispatcherInvoker = dispatcherInvoker ?? new InlineDispatcherInvoker();
        _recoveryStore = recoveryStore;
        // 8.185-T4 (S1): política global de redondeo para el Bs.S efectivo (opcional: sin servicio
        // rigen los defaults del sistema — ExactMath + piso activo ⇒ techo canónico, S7).
        _settingsService = settingsService;

        // Sync local property when exchange rate changes globally
        WeakReferenceMessenger.Default.Register<ExchangeRateChangedMessage>(this, (r, m) =>
        {
            var vm = (PosViewModel)r;
            vm.OnPropertyChanged(nameof(CurrentExchangeRate));
            vm.OnPropertyChanged(nameof(IsRateOutdated));

            // 8.143: los DTO de sugerencias no son observables; con una busqueda activa hay que
            // re-ejecutarla para que el popup derive los Bs.S con la tasa nueva.
            if (!string.IsNullOrWhiteSpace(vm.SearchText))
            {
                vm.ExecuteSearchAsync().SafeFireAndForget("PosViewModel.RateChangedResearch");
            }
        });

        WeakReferenceMessenger.Default.Register<PaymentMethodsChangedMessage>(this, async (r, m) =>
        {
            await ((PosViewModel)r).ReloadPaymentMethodsAsync();
        });

        // 8.185-T4 (S2b): políticas globales guardadas en Ajustes → aplicar en memoria y re-derivar
        // las sugerencias visibles (los DTO no son observables: se re-ejecuta la búsqueda activa,
        // mismo patrón que la tasa 8.143).
        WeakReferenceMessenger.Default.Register<PricingPoliciesChangedMessage>(this, (r, m) =>
        {
            var vm = (PosViewModel)r;
            vm.ApplyPricingPolicies(m.Value);

            if (!string.IsNullOrWhiteSpace(vm.SearchText))
            {
                vm.ExecuteSearchAsync().SafeFireAndForget("PosViewModel.PoliciesChangedResearch");
            }
        });

        // 8.185-T4 (S2c): override cash de un artículo guardado en el Catálogo → recomputar en
        // memoria el Bs.S del ítem visible, sin consultas ni re-búsqueda.
        WeakReferenceMessenger.Default.Register<ProductRoundingChangedMessage>(this, (r, m) =>
        {
            ((PosViewModel)r).ApplyProductRoundingOverride(m.ProductId, m.RoundingRuleCashBs);
        });

        // 8.185-T4 (S1): lectura única de la política global al iniciar.
        if (_settingsService != null)
        {
            LoadPricingPoliciesAsync().SafeFireAndForget("PosViewModel.LoadPricingPolicies");
        }
    }

    private async Task LoadPricingPoliciesAsync()
    {
        try
        {
            var policies = await _settingsService!.GetPricingPoliciesAsync() ?? new PricingPoliciesDto();
            ApplyPricingPolicies(policies);
        }
        catch (Exception ex)
        {
            // Fallback silencioso: rigen los defaults (ExactMath + piso activo ⇒ techo, S7).
            Core.Logging.ClientStateLogger.LogError($"Fallo al cargar las políticas globales de redondeo del POS: {ex.Message}", nameof(PosViewModel));
        }
    }

    private void ApplyPricingPolicies(PricingPoliciesDto policies)
    {
        _globalCashRoundingRule = policies.CashBsRoundingRule;
        _neverRoundDownCashBs = policies.EnforceMinimumMarginFloor;
    }

    /// <summary>
    /// 8.185-T4 (S2c): parchea la sugerencia visible del artículo afectado con el override recién
    /// guardado y recomputa su Bs.S efectivo en memoria. El reemplazo en la misma posición dispara
    /// el evento de colección que el popup necesita (el DTO no es observable).
    /// </summary>
    private void ApplyProductRoundingOverride(int productId, RoundingRuleType? roundingRuleCashBs)
    {
        void Apply()
        {
            for (int i = 0; i < Suggestions.Count; i++)
            {
                var suggestion = Suggestions[i];
                if (suggestion.Id != productId)
                {
                    continue;
                }

                suggestion.RoundingRuleCashBs = roundingRuleCashBs;
                RewriteEffectivePriceBsS(suggestion);
                Suggestions[i] = suggestion;
            }
        }

        _dispatcherInvoker.Invoke(Apply);
    }

    /// <summary>
    /// 8.185-T4 (S1): único punto del POS para el Bs.S efectivo de una sugerencia — regla RESUELTA
    /// del quick-info ?? política global del cliente, con el toggle vigente. Sin USD/tasa válidos
    /// conserva el snapshot del DTO (null/ExactMath ⇒ techo canónico 8.104, S7).
    /// </summary>
    private void RewriteEffectivePriceBsS(ProductQuickInfoDto item)
    {
        item.PriceBsS = item.PriceUSD > 0 && CurrentExchangeRate > 0
            ? Helpers.EffectivePriceVesHelper.EffectiveBsS(item.PriceUSD, CurrentExchangeRate, item.RoundingRuleCashBs ?? _globalCashRoundingRule, _neverRoundDownCashBs)
            : item.PriceBsS;
    }

    private readonly SemaphoreSlim _sessionInitializationGate = new(1, 1);

    public void ResetSession()
    {
        _recoveryCheckCompleted = false;

        Action clearAction = () =>
        {
            ActivePaymentMethods.Clear();
            Cart.CurrentSale = null;
            Cart.CartItems.Clear();
            RecentScannedProducts.Clear();
            SearchText = string.Empty;
            Suggestions.Clear();
        };

        Cart.PreserveRecoverySnapshotOnNextClear();
        _dispatcherInvoker.Invoke(clearAction);
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
                await RestoreOrStartSaleAsync();
            }
        }
        finally
        {
            _sessionInitializationGate.Release();
        }
    }

    private async Task RestoreOrStartSaleAsync()
    {
        if (_recoveryCheckCompleted)
        {
            await StartNewSaleAsync();
            return;
        }

        _recoveryCheckCompleted = true;

        var snapshot = _recoveryStore?.Load();

        if (snapshot == null || snapshot.ItemCount <= 0 || snapshot.Status != "Pending")
        {
            if (snapshot != null)
            {
                _recoveryStore?.Clear();
            }

            await StartNewSaleAsync();
            return;
        }

        string mensaje = $"Se detectó una venta sin finalizar del {snapshot.SavedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm} con {snapshot.ItemCount} artículo(s) por un total de ${snapshot.TotalUSD:0.00} USD. ¿Desea recuperarla?";
        bool recover = _dialogService?.ShowConfirm("Recuperar venta sin finalizar", mensaje) ?? false;

        if (recover)
        {
            try
            {
                var sale = await _salesService.GetSaleAsync(snapshot.SaleId);
                if (sale is { Status: "Pending" } && sale.Items != null && sale.Items.Count > 0)
                {
                    Cart.CurrentSale = sale;
                    return;
                }

                _recoveryStore?.Clear();
            }
            catch (Exception ex)
            {
                _recoveryCheckCompleted = false;
                Core.Logging.ClientStateLogger.LogError($"Error al recuperar la venta pendiente #{snapshot.SaleId}: {ex.Message}", nameof(PosViewModel));
                _dialogService?.ShowWarning("Recuperar venta sin finalizar", $"No se pudo recuperar la venta #{snapshot.SaleId}. La venta sigue pendiente; verifique la conexión y vuelva a entrar al Punto de Venta para reintentar.");
                return;
            }
        }
        else
        {
            _recoveryStore?.Clear();
        }

        await StartNewSaleAsync();
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

                _dispatcherInvoker.Invoke(updateAction);
                return;
            }
            catch (Exception ex)
            {
                if (_userSession != null && !_userSession.IsLoggedIn) return;

                if (attempt == maxRetries)
                {
                    Core.Logging.ClientStateLogger.LogError($"Error al cargar métodos de pago tras {maxRetries} intentos: {ex.Message}", nameof(PosViewModel));
                    _dialogService?.ShowError("Error de Conexión", $"No se pudieron cargar los métodos de pago tras {maxRetries} intentos. Verifique la conexión con el servidor e intente nuevamente.");
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

        IsProcessing = true;
        try
        {
            var newSale = await _salesService.StartSaleAsync(_userSession?.CurrentUser?.Id);
            Cart.CurrentSale = newSale;
            RecentScannedProducts.Clear();
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogError($"Error al iniciar la venta: {ex.Message}", nameof(PosViewModel));
            if (_dialogService != null) _dialogService.ShowError("Error de Venta", "No se pudo iniciar la venta. Verifique la conexión con el servidor e intente nuevamente.");
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

        void RunOnUI(Action action) => _dispatcherInvoker.Invoke(action);

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
                        // 8.143 + 8.185 (S1): derivar del único punto de cálculo en memoria; el
                        // snapshot PriceBsS solo es fallback sin USD o sin tasa válida.
                        RewriteEffectivePriceBsS(item);
                        Suggestions.Add(item);
                    }
                }

                HasSuggestions = Suggestions.Any();
                if (SelectedSuggestion != null) SelectedSuggestion = null;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogError($"Error al buscar productos '{term}': {ex.Message}", "PosViewModel");
            RunOnUI(() =>
            {
                Suggestions.Clear();
                Suggestions.Add(new ProductQuickInfoDto { Id = -2, Name = "Error de conexión. Reintente la búsqueda.", SKU = "-" });
                HasSuggestions = Suggestions.Any();
                if (SelectedSuggestion != null) SelectedSuggestion = null;
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
