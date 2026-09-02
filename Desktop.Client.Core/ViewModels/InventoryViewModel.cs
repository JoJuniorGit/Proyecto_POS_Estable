using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.Entities;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

public class PageNumberItem
{
    public int PageNumber { get; set; }
    public bool IsActive { get; set; }
}

public partial class InventoryViewModel : ObservableObject, IDisposable
{
    private readonly Desktop.Client.Services.IProductService _product_service;
    private readonly Desktop.Client.Services.IExchangeRateService _exchange_rate_service;
    private System.Threading.CancellationTokenSource? _cancellation_token_source;

    private ObservableCollection<ProductItemViewModel> _products = new();
    public ObservableCollection<ProductItemViewModel> Products
    {
        get => _products;
        set => SetProperty(ref _products, value);
    }

    private string _search_text = string.Empty;
    public string SearchText
    {
        get => _search_text;
        set
        {
            if (SetProperty(ref _search_text, value))
            {
                _ = RestartSearchTimerAsync();
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

    public bool IsSortedByName => SortBy.Equals("name", System.StringComparison.OrdinalIgnoreCase);
    public bool IsSortedBySku => SortBy.Equals("sku", System.StringComparison.OrdinalIgnoreCase);
    public bool IsSortedByStock => SortBy.Equals("stock", System.StringComparison.OrdinalIgnoreCase);
    public bool IsSortedByCost => SortBy.Equals("cost", System.StringComparison.OrdinalIgnoreCase);
    public bool IsSortedByPrice => SortBy.Equals("price", System.StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<PageNumberItem> PageNumbers { get; } = new();

    public bool CanGoFirst => CurrentPage > 1 && TotalPages > 1;
    public bool CanGoPrevious => CurrentPage > 1 && TotalPages > 1;
    public bool CanGoNext => CurrentPage < TotalPages && TotalPages > 1;
    public bool CanGoLast => CurrentPage < TotalPages && TotalPages > 1;

    [RelayCommand]
    public async Task Sort(string column)
    {
        if (string.IsNullOrWhiteSpace(column)) return;

        if (SortBy.Equals(column, System.StringComparison.OrdinalIgnoreCase))
        {
            IsSortDescending = !IsSortDescending;
        }
        else
        {
            SortBy = column.ToLower().Trim();
            IsSortDescending = false;
        }

        OnPropertyChanged(nameof(IsSortedByName));
        OnPropertyChanged(nameof(IsSortedBySku));
        OnPropertyChanged(nameof(IsSortedByStock));
        OnPropertyChanged(nameof(IsSortedByCost));
        OnPropertyChanged(nameof(IsSortedByPrice));

        await LoadDataAsync(false, targetPage: 1);
    }

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isScanning;

    [RelayCommand]
    private void ToggleWholesale()
    {
        ShowWholesale = !ShowWholesale;
    }

    [RelayCommand]
    private async Task FirstPage()
    {
        if (CanGoFirst)
        {
            await LoadDataAsync(false, targetPage: 1);
        }
    }

    [RelayCommand]
    private async Task PreviousPage()
    {
        if (CanGoPrevious)
        {
            await LoadDataAsync(false, targetPage: CurrentPage - 1);
        }
    }

    [RelayCommand]
    private async Task NextPage()
    {
        if (CanGoNext)
        {
            await LoadDataAsync(false, targetPage: CurrentPage + 1);
        }
    }

    [RelayCommand]
    private async Task LastPage()
    {
        if (CanGoLast)
        {
            await LoadDataAsync(false, targetPage: TotalPages);
        }
    }

    [RelayCommand]
    private async Task GoToPage(int page)
    {
        if (page >= 1 && page <= TotalPages && page != CurrentPage)
        {
            await LoadDataAsync(false, targetPage: page);
        }
    }

    [RelayCommand]
    private async Task SubmitGoToPage()
    {
        if (int.TryParse(TargetPageInput, out int target) && TotalPages > 0)
        {
            int clamped = Math.Clamp(target, 1, TotalPages);
            if (clamped != CurrentPage)
            {
                await LoadDataAsync(false, targetPage: clamped);
            }
            else
            {
                TargetPageInput = CurrentPage.ToString();
            }
        }
        else
        {
            TargetPageInput = CurrentPage > 0 ? CurrentPage.ToString() : "1";
        }
    }

    public void UpdatePageNumbers()
    {
        PageNumbers.Clear();

        if (TotalPages <= 0 || TotalCount == 0)
        {
            CurrentPage = 0;
            PageSummary = "Página 0 de 0 (0 productos)";
            TargetPageInput = "0";
            NotifyPaginationCanExecute();
            return;
        }

        if (CurrentPage <= 0) CurrentPage = 1;
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;

        int startPage = Math.Max(1, CurrentPage - 2);
        int endPage = Math.Min(TotalPages, CurrentPage + 2);

        for (int p = startPage; p <= endPage; p++)
        {
            PageNumbers.Add(new PageNumberItem
            {
                PageNumber = p,
                IsActive = (p == CurrentPage)
            });
        }

        TargetPageInput = CurrentPage.ToString();
        NotifyPaginationCanExecute();
    }

    public void NotifyPaginationCanExecute()
    {
        OnPropertyChanged(nameof(CanGoFirst));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoLast));
    }

    public decimal CurrentRate => _exchange_rate_service.CurrentRate;
    public UserSession? UserSession { get; }
    private readonly IDialogService? _dialog_service;
    
    public InventoryViewModel(Desktop.Client.Services.IProductService product_service, Desktop.Client.Services.IExchangeRateService exchange_rate_service, Desktop.Client.Services.UserSession? userSession = null, IDialogService? dialog_service = null)
    {
        _product_service = product_service;
        _exchange_rate_service = exchange_rate_service;
        UserSession = userSession;
        _dialog_service = dialog_service;

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
            _ = LoadDataAsync(false);
        }
    }

    public async Task EnsureLoadedAsync()
    {
        if ((UserSession == null || UserSession.IsLoggedIn) && !Products.Any())
        {
            await LoadDataAsync(false);
        }
    }

    private readonly System.Threading.SemaphoreSlim _load_lock = new(1, 1);

    private async Task TaskWithDelay(int ms, System.Threading.CancellationToken token)
    {
        await Task.Delay(ms, token);
    }

    private async Task RestartSearchTimerAsync()
    {
        var newCts = new System.Threading.CancellationTokenSource();
        var oldCts = System.Threading.Interlocked.Exchange(ref _cancellation_token_source, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var token = newCts.Token;

        try
        {
            var text = _search_text?.Trim() ?? string.Empty;

            // Requisito: Cuando se borra el contenido (por ejemplo con la 'x' o borrando el texto),
            // se reinicia inmediatamente mostrando la lista completa desde la página 1.
            if (string.IsNullOrEmpty(text))
            {
                await LoadDataAsync(false, targetPage: 1, token: token);
                return;
            }

            // Solo buscar con 2 o más caracteres
            if (text.Length < 2)
            {
                return; 
            }

            // 40ms Debounce para escritura rápida
            await Task.Delay(40, token);
            
            await LoadDataAsync(false, targetPage: 1, token: token);
        }
        catch (OperationCanceledException) { }
    }

    private string _selectedStatusFilter = "active";
    public string SelectedStatusFilter
    {
        get => _selectedStatusFilter;
        set
        {
            if (SetProperty(ref _selectedStatusFilter, value))
            {
                _ = LoadDataAsync(false);
            }
        }
    }

    private async Task LoadDataAsync(bool incremental, int? targetPage = null, System.Threading.CancellationToken token = default)
    {
        if (UserSession != null && !UserSession.IsLoggedIn) return;

        try
        {
            await _load_lock.WaitAsync(token);
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

            if (_exchange_rate_service.CurrentRate <= 0)
            {
                await _exchange_rate_service.GetCurrentRateAsync();
            }

            var queryText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
            var result = await _product_service.GetPagedAsync(queryText, CurrentPage, PageSize, statusFilter: SelectedStatusFilter, sortBy: SortBy, isDescending: IsSortDescending, token: token);
            
            // Build ProductItemViewModel instances on background thread to prevent UI thread stutter
            var newItems = new System.Collections.Generic.List<ProductItemViewModel>();
            foreach (var dto in result.Items)
            {
                var itemVm = new ProductItemViewModel(dto, _exchange_rate_service, OnProductItemChanged);
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

            if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(UpdateState);
            }
            else
            {
                UpdateState();
            }
        }
        catch (OperationCanceledException) { }
        catch (System.Exception ex)
        {
            _dialog_service?.ShowError("Error de Carga", $"Error al cargar productos: {ex.Message}");
        }
        finally
        {
            IsSearching = false;
            try
            {
                _load_lock.Release();
            }
            catch (ObjectDisposedException) { }
            catch (System.Threading.SemaphoreFullException) { }
        }
    }

    private async Task MergeProductsAsync(System.Threading.CancellationToken token = default)
    {
        try
        {
            await _load_lock.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            IsSearching = true;

            var queryText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
            var result = await _product_service.GetPagedAsync(queryText, _currentPage, PageSize, statusFilter: SelectedStatusFilter, token: token);

            var fetchedDict = result.Items.ToDictionary(dto => dto.SKU, dto => dto);
            var existingSkus = Products.Select(p => p.SKU).ToHashSet();
            var newItemsToAdd = new System.Collections.Generic.List<ProductItemViewModel>();

            foreach (var dto in result.Items)
            {
                if (!existingSkus.Contains(dto.SKU))
                {
                    var itemVm = new ProductItemViewModel(dto, _exchange_rate_service, OnProductItemChanged);
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

            if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(UpdateMerge);
            }
            else
            {
                UpdateMerge();
            }
        }
        catch (OperationCanceledException) { }
        catch (System.Exception ex)
        {
            _dialog_service?.ShowError("Error de Catálogo", $"Error al actualizar catálogo: {ex.Message}");
        }
        finally
        {
            IsSearching = false;
            try
            {
                _load_lock.Release();
            }
            catch (ObjectDisposedException) { }
            catch (System.Threading.SemaphoreFullException) { }
        }
    }

    public void Dispose()
    {
        var oldCts = System.Threading.Interlocked.Exchange(ref _cancellation_token_source, null);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _load_lock.Dispose();
        }
        catch (ObjectDisposedException) { }

        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private async void OnProductItemChanged(ProductItemViewModel item)
    {
        try
        {
            var product = await _product_service.GetByIdAsync(item.Id);
            if (product != null)
            {
                var dto = item.GetDto();
                product.ProfitPercentage = dto.ProfitPercentage;
                product.PriceUSD = dto.PriceUSD;
                product.PriceBsS = dto.PriceBsS;
                await _product_service.UpdateAsync(product);
            }
        }
        catch (Exception ex)
        {
            _dialog_service?.ShowWarning("Error de Auto-Guardado", $"Error al guardar automáticamente el producto {item.Id}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SetStatusFilter(string filter)
    {
        SelectedStatusFilter = filter;
    }

    [RelayCommand]
    private async Task TogglePauseProduct(ProductItemViewModel item)
    {
        try
        {
            bool newActive = !item.IsActive;
            await _product_service.SetStatusAsync(item.Id, newActive, false);
            item.IsActive = newActive;
            item.IsDeleted = false;

            if (SelectedStatusFilter != "all")
            {
                Products.Remove(item);
            }
        }
        catch (System.Exception ex)
        {
            _dialog_service?.ShowError("Error de Estado", $"Error al cambiar estado del producto: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RestoreProduct(ProductItemViewModel item)
    {
        try
        {
            await _product_service.RestoreAsync(item.Id);
            item.IsActive = true;
            item.IsDeleted = false;
            
            if (SelectedStatusFilter != "all")
            {
                Products.Remove(item);
            }
            _dialog_service?.ShowSuccessDialog($"Producto '{item.Name}' restaurado exitosamente a estado Activo.");
        }
        catch (System.Exception ex)
        {
            _dialog_service?.ShowError("Error al Restaurar", $"Error restaurando producto: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteProduct(ProductItemViewModel item)
    {
        if (_dialog_service == null) return;

        // Paso 1: Preguntar si desea pausar/deshabilitar el producto
        bool wantPause = _dialog_service.ShowConfirm(
            "Pausar / Deshabilitar Producto",
            $"¿Desea pausar/deshabilitar el producto '{item.Name}'?\n\n(El producto quedará Inactivo y podrá reactivarse posteriormente)");

        if (wantPause)
        {
            await TogglePauseProduct(item);
            return;
        }

        // Paso 2: Si no deseaba pausar, preguntar si desea eliminar definitivamente / archivar
        bool wantDelete = _dialog_service.ShowConfirm(
            "Eliminar Producto",
            $"¿Desea eliminar definitivamente el producto '{item.Name}'?\n\n(Si el producto posee ventas pasadas en el historial, se archivará automáticamente para auditoría contable)");

        if (wantDelete)
        {
            try
            {
                var res = await _product_service.DeleteAsync(item.Id, hardDelete: true);
                if (res == "hard_deleted")
                {
                    Products.Remove(item);
                    _dialog_service.ShowSuccessDialog($"Producto '{item.Name}' eliminado permanentemente de la base de datos.");
                }
                else
                {
                    item.IsActive = false;
                    item.IsDeleted = true;
                    if (SelectedStatusFilter == "active")
                    {
                        Products.Remove(item);
                    }
                    _dialog_service.ShowWarning("Archivado por Auditoría", $"El producto '{item.Name}' posee ventas registradas en el historial. Ha sido archivado en 'Eliminados - Archivo Contable' para preservar la integridad de los datos.");
                }
            }
            catch (System.Exception ex)
            {
                _dialog_service.ShowError("Error al Eliminar", $"Error al eliminar producto: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task OpenAddProduct()
    {
        if (_dialog_service == null) return;
        var _dialogVm = new ProductDialogViewModel(_product_service, _exchange_rate_service, null, UserSession, _dialog_service);
        
        if (_dialog_service.ShowProductDialog(_dialogVm) == true)
        {
            try
            {
                var createdProduct = await _product_service.CreateAsync(_dialogVm.ResultProduct);
                var newDto = MapToDto(createdProduct);
                
                // Add to list and select
                var viewModelItem = new ProductItemViewModel(newDto, _exchange_rate_service, OnProductItemChanged);
                Products.Insert(0, viewModelItem);
                
                _dialog_service.ShowSuccessDialog($"Producto '{createdProduct.Name}' agregado exitosamente.");
            }
            catch (System.Exception ex)
            {
                _dialog_service.ShowError("Error al Agregar", $"Error al agregar producto: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task EditProduct(ProductItemViewModel item)
    {
        if (_dialog_service == null) return;
        try
        {
            var product = await _product_service.GetByIdAsync(item.Id);
            if (product == null)
            {
                _dialog_service.ShowWarning("Producto no encontrado", "No se encontró el producto especificado.");
                return;
            }

            var _dialogVm = new ProductDialogViewModel(_product_service, _exchange_rate_service, product, UserSession, _dialog_service);
            
            if (_dialog_service.ShowProductDialog(_dialogVm) == true)
            {
                await _product_service.UpdateAsync(_dialogVm.ResultProduct);
                var updatedDto = MapToDto(_dialogVm.ResultProduct);
                
                var index = Products.IndexOf(item);
                if (index != -1)
                {
                    Products[index] = new ProductItemViewModel(updatedDto, _exchange_rate_service, OnProductItemChanged);
                }
                
                _dialog_service.ShowSuccessDialog($"Producto '{_dialogVm.ResultProduct.Name}' actualizado con éxito.");
            }
        }
        catch (System.Exception ex)
        {
            _dialog_service.ShowError("Error al Editar", $"Error al editar el producto: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AdjustStock(ProductItemViewModel item)
    {
        if (_dialog_service == null) return;
        if (!item.CanAdjustStock)
        {
            _dialog_service.ShowWarning("Ajuste no permitido", item.AdjustStockToolTip);
            return;
        }
        try
        {
            var product = await _product_service.GetByIdAsync(item.Id);
            if (product == null)
            {
                _dialog_service.ShowWarning("Producto no encontrado", "No se encontró el producto especificado.");
                return;
            }

            var (success, qtyChange, reason) = _dialog_service.ShowAdjustStockDialog(MapToDto(product));
            if (success)
            {
                await _product_service.AdjustStockAsync(product.Id, qtyChange, reason);
                // The item in our list needs to show updated stock
                var dto = item.GetDto();
                dto.StockQuantity += qtyChange;
                
                var _index = Products.IndexOf(item);
                if (_index != -1)
                {
                    Products[_index] = new ProductItemViewModel(dto, _exchange_rate_service, OnProductItemChanged);
                }
            }
        }
        catch (System.Exception _ex)
        {
            _dialog_service.ShowError("Error al Ajustar Stock", $"Error al ajustar stock: {_ex.Message}");
        }
    }

    [RelayCommand]
    private async Task Scan(string term)
    {
        IsScanning = true;
        try
        {
            var _product = await _product_service.GetQuickInfoAsync(term);
            if (_product != null)
            {
                _dialog_service?.ShowInfo("Verificación Rápida", $"Escaneado: {_product.Name}\nPrecio USD: ${_product.PriceUSD:N2}\nStock: {_product.StockQuantity}");
            }
            else
            {
                _dialog_service?.ShowWarning("Búsqueda por Escaneo", $"Producto no encontrado con el código: {term}");
            }
        }
        catch (System.Exception _ex)
        {
            _dialog_service?.ShowError("Error de Escaneo", $"Fallo en la lectura del código: {_ex.Message}");
        }
        finally
        {
            IsScanning = false;
        }
    }

    private Core.DTOs.ProductDto MapToDto(Product p)
    {
        return new Core.DTOs.ProductDto
        {
            Id = p.Id,
            Name = p.Name,
            SKU = p.SKU,
            Description = p.Description,
            PriceUSD = p.PriceUSD,
            PriceRetailUSD = p.PriceRetailUSD,
            PriceWholesaleUSD = p.PriceWholesaleUSD,
            CostPriceUSD = p.CostPriceUSD,
            ProfitMarginRetail = p.ProfitMarginRetail,
            ProfitMarginWholesale = p.ProfitMarginWholesale,
            MinWholesaleQuantity = p.MinWholesaleQuantity,
            HasWholesale = p.HasWholesale,
            IsFractional = p.IsFractional,
            PriceBsS = p.PriceBsS,
            Cost = p.Cost,
            StockQuantity = p.StockQuantity,
            ProfitPercentage = p.ProfitPercentage,
            UnitOfMeasure = p.UnitOfMeasure,
            LowStockThreshold = p.LowStockThreshold,
            IsCashAdvance = p.IsCashAdvance,
            IsActive = p.IsActive,
            IsDeleted = p.IsDeleted,
            ReservedQuantity = p.ReservedQuantity,
            IsGroupHeader = p.IsGroupHeader,
            ParentProductId = p.ParentProductId,
            GroupKey = p.GroupKey
        };
    }

}
