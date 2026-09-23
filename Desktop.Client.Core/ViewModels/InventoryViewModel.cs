using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.Common;
using Core.Entities;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class InventoryViewModel : ObservableObject, IDisposable
{
    private readonly IProductService _productService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly IDialogService? _dialogService;
    private readonly IDispatcherInvoker _dispatcherInvoker;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public UserSession? UserSession { get; }
    public decimal CurrentRate => _exchangeRateService.CurrentRate;

    private ObservableCollection<ProductItemViewModel> _products = new();
    public ObservableCollection<ProductItemViewModel> Products
    {
        get => _products;
        set => SetProperty(ref _products, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RestartSearchTimerAsync().SafeFireAndForget("InventoryViewModel.RestartSearchTimer");
            }
        }
    }

    private int _currentPage = 1;
    public int CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    private const int PageSize = 25; // Requisito: Paginación de 25 elementos

    private string _selectedCurrency = "Bs.S"; // Requisito: "Bs.S" por defecto al entrar al catálogo
    public string SelectedCurrency
    {
        get => _selectedCurrency;
        set
        {
            if (SetProperty(ref _selectedCurrency, value))
            {
                OnPropertyChanged(nameof(RetailPriceHeader));
                OnPropertyChanged(nameof(WholesalePriceHeader));
                foreach (var product in Products)
                {
                    product.NotifyCurrencyChanged(value);
                }
            }
        }
    }

    public string RetailPriceHeader => $"Precio Detal ({SelectedCurrency})";
    public string WholesalePriceHeader => $"Precio Mayor ({SelectedCurrency})";

    [ObservableProperty]
    private bool _showWholesale = false; // Requisito: Desactivado por defecto al entrar al catálogo

    public string WholesaleButtonText => ShowWholesale ? "Ocultar Precios al Mayor" : "Mostrar Precios al Mayor";

    partial void OnShowWholesaleChanged(bool value)
    {
        OnPropertyChanged(nameof(WholesaleButtonText));
    }

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private string _pageSummary = "Página 1 de 1 (0 productos)";

    [ObservableProperty]
    private string _targetPageInput = "1";

    [ObservableProperty]
    private string _sortBy = "name";

    [ObservableProperty]
    private bool _isSortDescending = false;

    public bool IsSortedByName => SortBy.Equals("name", StringComparison.OrdinalIgnoreCase);
    public bool IsSortedBySku => SortBy.Equals("sku", StringComparison.OrdinalIgnoreCase);
    public bool IsSortedByStock => SortBy.Equals("stock", StringComparison.OrdinalIgnoreCase);
    public bool IsSortedByCost => SortBy.Equals("cost", StringComparison.OrdinalIgnoreCase);
    public bool IsSortedByPrice => SortBy.Equals("price", StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<PageNumberItem> PageNumbers { get; } = new();

    public bool CanGoFirst => CurrentPage > 1 && TotalPages > 1;
    public bool CanGoPrevious => CurrentPage > 1 && TotalPages > 1;
    public bool CanGoNext => CurrentPage < TotalPages && TotalPages > 1;
    public bool CanGoLast => CurrentPage < TotalPages && TotalPages > 1;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isRefreshing;

    private string _selectedStatusFilter = "active";
    public string SelectedStatusFilter
    {
        get => _selectedStatusFilter;
        set
        {
            if (SetProperty(ref _selectedStatusFilter, value))
            {
                LoadDataAsync(false).SafeFireAndForget("InventoryViewModel.StatusFilterChanged");
            }
        }
    }

    public InventoryViewModel(
        IProductService productService,
        IExchangeRateService exchangeRateService,
        UserSession? userSession = null,
        IDialogService? dialogService = null,
        IDispatcherInvoker? dispatcherInvoker = null)
    {
        _productService = productService;
        _exchangeRateService = exchangeRateService;
        UserSession = userSession;
        _dialogService = dialogService;
        _dispatcherInvoker = dispatcherInvoker ?? new InlineDispatcherInvoker();

        WeakReferenceMessenger.Default.Register<ExchangeRateChangedMessage>(this, (r, m) =>
        {
            OnPropertyChanged(nameof(CurrentRate));
            foreach (var product in Products)
            {
                product.UpdateExchangeRate();
            }
        });

        WeakReferenceMessenger.Default.Register<CatalogUpdatedMessage>(this, async (r, m) =>
        {
            await MergeProductsAsync();
        });

        if (UserSession == null || UserSession.IsLoggedIn)
        {
            LoadDataAsync(false).SafeFireAndForget("InventoryViewModel.InitialLoad");
        }
    }

    public async Task EnsureLoadedAsync()
    {
        if ((UserSession == null || UserSession.IsLoggedIn) && !Products.Any())
        {
            await LoadDataAsync(false);
        }
    }

    [RelayCommand]
    public async Task Refresh()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            await _exchangeRateService.GetCurrentRateAsync();
            OnPropertyChanged(nameof(CurrentRate));
            await LoadDataAsync(false, targetPage: CurrentPage > 0 ? CurrentPage : 1);
        }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Error de Actualización", $"No se pudo actualizar el catálogo: {ex.Message}");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void ToggleWholesale()
    {
        ShowWholesale = !ShowWholesale;
    }

    private async Task LoadDataAsync(bool incremental, int? targetPage = null, CancellationToken token = default)
    {
        if (UserSession != null && !UserSession.IsLoggedIn) return;

        try
        {
            await _loadLock.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        
        try
        {
            IsSearching = true;
            
            if (targetPage.HasValue)
            {
                CurrentPage = targetPage.Value;
            }
            else if (!incremental)
            {
                CurrentPage = 1;
            }
            else
            {
                CurrentPage++;
            }

            if (_exchangeRateService.CurrentRate <= 0)
            {
                await _exchangeRateService.GetCurrentRateAsync();
            }

            var queryText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
            var result = await _productService.GetPagedAsync(queryText, CurrentPage, PageSize, statusFilter: SelectedStatusFilter, sortBy: SortBy, isDescending: IsSortDescending, token: token);
            
            // Build ProductItemViewModel instances on background thread to prevent UI thread stutter
            var newItems = new System.Collections.Generic.List<ProductItemViewModel>();
            foreach (var dto in result.Items)
            {
                var itemVm = new ProductItemViewModel(dto, _exchangeRateService, OnProductItemChanged);
                itemVm.NotifyCurrencyChanged(SelectedCurrency);
                newItems.Add(itemVm);
            }

            var totalPages = result.TotalCount > 0 ? (int)Math.Ceiling((double)result.TotalCount / PageSize) : 0;
            var pageSummary = totalPages > 0
                ? $"Página {CurrentPage} de {totalPages} ({result.TotalCount} productos)"
                : "Página 0 de 0 (0 productos)";

            void UpdateState()
            {
                if (!incremental)
                {
                    Products.Clear();
                }

                foreach (var item in newItems)
                {
                    Products.Add(item);
                }

                HasMore = result.HasMore;
                TotalCount = result.TotalCount;
                TotalPages = totalPages;
                PageSummary = pageSummary;
                UpdatePageNumbers();
            }

            if (!_dispatcherInvoker.CheckAccess())
            {
                _dispatcherInvoker.Invoke(UpdateState);
            }
            else
            {
                UpdateState();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Error de Carga", $"Error al cargar productos: {ex.Message}");
        }
        finally
        {
            IsSearching = false;
            try
            {
                _loadLock.Release();
            }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }
    }

    private async Task MergeProductsAsync(CancellationToken token = default)
    {
        try
        {
            await _loadLock.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            IsSearching = true;

            var queryText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
            var result = await _productService.GetPagedAsync(queryText, _currentPage, PageSize, statusFilter: SelectedStatusFilter, token: token);

            var fetchedDict = result.Items.ToDictionary(dto => dto.SKU, dto => dto);
            var existingSkus = Products.Select(p => p.SKU).ToHashSet();
            var newItemsToAdd = new System.Collections.Generic.List<ProductItemViewModel>();

            foreach (var dto in result.Items)
            {
                if (!existingSkus.Contains(dto.SKU))
                {
                    var itemVm = new ProductItemViewModel(dto, _exchangeRateService, OnProductItemChanged);
                    itemVm.NotifyCurrencyChanged(SelectedCurrency);
                    newItemsToAdd.Add(itemVm);
                }
            }

            void UpdateMerge()
            {
                // 1. Update existing items in place
                foreach (var item in Products.ToList())
                {
                    if (fetchedDict.TryGetValue(item.SKU, out var updatedDto))
                    {
                        item.UpdateFromDto(updatedDto);
                    }
                }

                // 2. Add new items
                foreach (var newItem in newItemsToAdd)
                {
                    Products.Add(newItem);
                }

                HasMore = result.HasMore;
            }

            if (!_dispatcherInvoker.CheckAccess())
            {
                _dispatcherInvoker.Invoke(UpdateMerge);
            }
            else
            {
                UpdateMerge();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Error de Catálogo", $"Error al actualizar catálogo: {ex.Message}");
        }
        finally
        {
            IsSearching = false;
            try
            {
                _loadLock.Release();
            }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
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
            _loadLock.Dispose();
        }
        catch (ObjectDisposedException) { }

        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
