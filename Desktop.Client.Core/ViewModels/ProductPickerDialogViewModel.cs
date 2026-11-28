using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Core.Logging;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>Opción de filtro de categoría del selector de productos (Id nulo = todas).</summary>
public sealed record ProductPickerCategoryOption(int? Id, string Label);

/// <summary>Producto seleccionado para un alcance Products; se comparte con el alta de conteo.</summary>
public sealed record ProductPickerSelection(int Id, string Sku, string Name);

/// <summary>Fila del selector; solo expone SKU/nombre para no romper el conteo ciego.</summary>
public sealed class ProductPickerRowViewModel : ObservableObject
{
    private readonly Action<ProductPickerRowViewModel> _selectionChanged;
    private bool _isSelected;

    public ProductPickerRowViewModel(
        ProductDto product,
        bool isSelected,
        Action<ProductPickerRowViewModel> selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(selectionChanged);

        ProductId = product.Id;
        Sku = product.SKU;
        Name = product.Name;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public int ProductId { get; }
    public string Sku { get; }
    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged(this);
            }
        }
    }
}

/// <summary>
/// WU3: selector reutilizable de productos para el alcance del conteo. La búsqueda usa el endpoint
/// paginado de productos con filtro activo y categoría; la UI solo muestra SKU/nombre (ciego).
/// </summary>
public partial class ProductPickerDialogViewModel : ObservableObject, IDisposable
{
    public const int MaxSelection = 1000;
    private const int PageSize = 50;

    private readonly IProductService _productService;
    private readonly ICategoryService? _categoryService;
    private readonly Dictionary<int, ProductPickerSelection> _selected = new();
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _categoryAddCts;
    private bool _initialized;
    private bool _suppressSelectionCallback;
    private bool _suppressCategoryReload;
    private int _disposed;

    public event Action<bool>? RequestClose;

    public ObservableCollection<ProductPickerCategoryOption> Categories { get; } = new();
    public ObservableCollection<ProductPickerRowViewModel> Results { get; } = new();
    public ObservableCollection<ProductPickerSelection> SelectedProducts { get; } = new();
    public ObservableCollection<PageNumberItem> PageNumbers { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ProductPickerCategoryOption? _selectedCategory;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _targetPageInput = "1";

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasResults => Results.Count > 0;
    public bool HasSelection => SelectedProducts.Count > 0;
    public int SelectedCount => SelectedProducts.Count;
    public string SelectionCounterText => $"Seleccionados: {SelectedCount} / {MaxSelection}";
    public string PageText => $"Página {Page} de {TotalPages}";
    public string PageSummary => $"Página {Page} de {TotalPages} · {TotalCount} productos";
    public bool HasPreviousPage => CanGoPrevious;
    public bool HasNextPage => CanGoNext;
    public bool CanGoFirst => Page > 1 && TotalPages > 1;
    public bool CanGoPrevious => Page > 1 && TotalPages > 1;
    public bool CanGoNext => Page < TotalPages && TotalPages > 1;
    public bool CanGoLast => Page < TotalPages && TotalPages > 1;
    public bool CanConfirm => SelectedCount > 0;
    public string ConfirmButtonText => SelectedCount > 0
        ? $"Confirmar selección ({SelectedCount})"
        : "Confirmar selección";
    public string SelectionCounterBackground => SelectedCount >= MaxSelection - 100
        ? "#FEF3C7"
        : "#F5F3FF";
    public string SelectionCounterForeground => SelectedCount >= MaxSelection - 100
        ? "#B45309"
        : "#5B21B6";
    public string SelectionCounterBorder => SelectedCount >= MaxSelection - 100
        ? "#FCD34D"
        : "#DDD6FE";
    public bool CanAddCategory => SelectedCategory?.Id is not null && !IsBusy;
    public string AddCategoryToolTip => SelectedCategory?.Id is not null
        ? "Agregar todos los productos de la categoría seleccionada"
        : "Selecciona una categoría específica para agregar todos sus productos";

    public bool? PageSelectionState
    {
        get
        {
            if (Results.Count == 0)
            {
                return false;
            }

            var selectedOnPage = Results.Count(row => row.IsSelected);
            if (selectedOnPage == 0)
            {
                return false;
            }

            return selectedOnPage == Results.Count ? true : null;
        }
        set
        {
            if (value == PageSelectionState)
            {
                return;
            }

            if (PageSelectionState is null)
            {
                SetPageSelection(true);
                return;
            }

            if (value.HasValue)
            {
                SetPageSelection(value.Value);
            }
        }
    }

    public bool IsPageSelected
    {
        get => PageSelectionState == true;
        set
        {
            if (value == IsPageSelected)
            {
                return;
            }

            SetPageSelection(value);
        }
    }

    public ProductPickerDialogViewModel(
        IProductService productService,
        ICategoryService? categoryService = null,
        IEnumerable<ProductPickerSelection>? preselected = null)
    {
        ArgumentNullException.ThrowIfNull(productService);

        _productService = productService;
        _categoryService = categoryService;

        if (preselected is not null)
        {
            foreach (var item in preselected.DistinctBy(item => item.Id))
            {
                _selected[item.Id] = item;
            }
        }

        SyncSelectedProducts();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || _disposed != 0)
        {
            return;
        }

        _initialized = true;
        await LoadCategoriesAsync(cancellationToken);
        await LoadPageAsync(1, cancellationToken);
    }

    [RelayCommand]
    private Task SearchAsync() => LoadPageAsync(1);

    [RelayCommand]
    private Task ClearSearchAsync()
    {
        SearchText = string.Empty;
        return LoadPageAsync(1);
    }

    [RelayCommand]
    private Task PreviousPageAsync() => HasPreviousPage
        ? LoadPageAsync(Page - 1)
        : Task.CompletedTask;

    [RelayCommand]
    private Task NextPageAsync() => HasNextPage
        ? LoadPageAsync(Page + 1)
        : Task.CompletedTask;

    [RelayCommand]
    private Task FirstPageAsync() => LoadPageAsync(1);

    [RelayCommand]
    private Task LastPageAsync() => TotalPages > 1
        ? LoadPageAsync(TotalPages)
        : Task.CompletedTask;

    [RelayCommand]
    private Task GoToPageAsync(int page) => page >= 1 && page <= TotalPages && page != Page
        ? LoadPageAsync(page)
        : Task.CompletedTask;

    [RelayCommand]
    private Task SubmitGoToPageAsync()
    {
        if (int.TryParse(TargetPageInput, out var target) && TotalPages > 0)
        {
            var clamped = Math.Clamp(target, 1, TotalPages);
            if (clamped != Page)
            {
                return LoadPageAsync(clamped);
            }
        }

        TargetPageInput = Page.ToString();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void RemoveSelected(ProductPickerSelection? item)
    {
        if (item is null)
        {
            return;
        }

        _selected.Remove(item.Id);
        var row = Results.FirstOrDefault(candidate => candidate.ProductId == item.Id);
        if (row is not null && row.IsSelected)
        {
            _suppressSelectionCallback = true;
            row.IsSelected = false;
            _suppressSelectionCallback = false;
        }

        SyncSelectedProducts();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _selected.Clear();
        _suppressSelectionCallback = true;
        foreach (var row in Results)
        {
            row.IsSelected = false;
        }
        _suppressSelectionCallback = false;
        SyncSelectedProducts();
    }

    [RelayCommand]
    private async Task AddCategoryProductsAsync()
    {
        if (SelectedCategory?.Id is not int categoryId || IsBusy || _disposed != 0)
        {
            return;
        }

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _categoryAddCts, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var token = newCts.Token;
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var pageNumber = 1;
            var hasMore = true;
            while (hasMore && _selected.Count < MaxSelection && !token.IsCancellationRequested)
            {
                var result = await _productService.GetPagedAsync(
                    null,
                    pageNumber,
                    100,
                    "active",
                    "name",
                    false,
                    token,
                    categoryId);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                foreach (var product in result.Items.Where(IsCountable))
                {
                    if (_selected.Count >= MaxSelection)
                    {
                        ErrorMessage = $"Máximo {MaxSelection} productos seleccionados.";
                        break;
                    }

                    if (!_selected.ContainsKey(product.Id))
                    {
                        _selected[product.Id] = new ProductPickerSelection(
                            product.Id,
                            product.SKU,
                            product.Name);
                    }
                }

                hasMore = result.HasMore;
                pageNumber++;
            }

            _suppressSelectionCallback = true;
            foreach (var row in Results)
            {
                if (_selected.ContainsKey(row.ProductId) && !row.IsSelected)
                {
                    row.IsSelected = true;
                }
            }
            _suppressSelectionCallback = false;

            SyncSelectedProducts();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ClientStateLogger.LogError(
                $"Fallo al agregar los productos de la categoría: {ex.Message}",
                nameof(ProductPickerDialogViewModel));
            ErrorMessage = "No se pudieron agregar los productos de la categoría.";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm)
        {
            return;
        }

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    partial void OnSelectedCategoryChanged(ProductPickerCategoryOption? value)
    {
        OnPropertyChanged(nameof(CanAddCategory));
        OnPropertyChanged(nameof(AddCategoryToolTip));

        if (_initialized && !_suppressCategoryReload && _disposed == 0)
        {
            LoadPageAsync(1).SafeFireAndForget("ProductPickerDialogViewModel.Category");
        }
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAddCategory));

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Filtro compartido con el backend: solo productos inventariables.</summary>
    public static bool IsCountable(ProductDto product) =>
        product.IsActive
        && !product.IsDeleted
        && !product.IsCashAdvance
        && !(product.IsGroupHeader && !product.IsStockShared)
        && !(product.ParentProductId.HasValue && product.ParentIsStockShared);

    private async Task LoadCategoriesAsync(CancellationToken cancellationToken)
    {
        Categories.Clear();
        Categories.Add(new ProductPickerCategoryOption(null, "Todas las categorías"));

        if (_categoryService is not null)
        {
            try
            {
                var categories = await _categoryService.GetCategoriesAsync(cancellationToken);
                foreach (var category in categories.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
                {
                    Categories.Add(new ProductPickerCategoryOption(category.Id, category.Path));
                }
            }
            catch (Exception ex)
            {
                ClientStateLogger.LogWarning(
                    $"No se pudieron cargar las categorías del selector de productos: {ex.Message}",
                    nameof(ProductPickerDialogViewModel));
            }
        }

        _suppressCategoryReload = true;
        SelectedCategory = Categories[0];
        _suppressCategoryReload = false;
    }

    private async Task LoadPageAsync(int page, CancellationToken cancellationToken = default)
    {
        var newCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var token = newCts.Token;
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var result = await _productService.GetPagedAsync(
                string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                page,
                PageSize,
                "active",
                "name",
                isDescending: false,
                token: token,
                categoryId: SelectedCategory?.Id);

            if (token.IsCancellationRequested)
            {
                return;
            }

            Page = page;
            TotalCount = result.TotalCount;
            TotalPages = Math.Max(1, (int)Math.Ceiling(result.TotalCount / (double)PageSize));

            Results.Clear();
            foreach (var product in result.Items.Where(IsCountable))
            {
                Results.Add(new ProductPickerRowViewModel(
                    product,
                    _selected.ContainsKey(product.Id),
                    OnRowSelectionChanged));
            }

            UpdatePageNumbers();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ClientStateLogger.LogError(
                $"Fallo al cargar productos para el selector de alcance: {ex.Message}",
                nameof(ProductPickerDialogViewModel));
            ErrorMessage = "No se pudieron cargar los productos. Intente nuevamente.";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    private void SetPageSelection(bool selected)
    {
        _suppressSelectionCallback = true;
        try
        {
            foreach (var row in Results)
            {
                if (row.IsSelected == selected)
                {
                    continue;
                }

                if (selected)
                {
                    if (_selected.Count >= MaxSelection)
                    {
                        ErrorMessage = $"Máximo {MaxSelection} productos seleccionados.";
                        break;
                    }

                    _selected[row.ProductId] = new ProductPickerSelection(row.ProductId, row.Sku, row.Name);
                }
                else
                {
                    _selected.Remove(row.ProductId);
                }

                row.IsSelected = selected;
            }
        }
        finally
        {
            _suppressSelectionCallback = false;
        }

        SyncSelectedProducts();
    }

    private void OnRowSelectionChanged(ProductPickerRowViewModel row)
    {
        if (_suppressSelectionCallback)
        {
            return;
        }

        if (row.IsSelected)
        {
            if (_selected.Count >= MaxSelection)
            {
                _suppressSelectionCallback = true;
                row.IsSelected = false;
                _suppressSelectionCallback = false;
                ErrorMessage = $"Máximo {MaxSelection} productos seleccionados.";
                return;
            }

            _selected[row.ProductId] = new ProductPickerSelection(row.ProductId, row.Sku, row.Name);
        }
        else
        {
            _selected.Remove(row.ProductId);
        }

        SyncSelectedProducts();
    }

    private void SyncSelectedProducts()
    {
        SelectedProducts.Clear();
        foreach (var item in _selected.Values
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Sku, StringComparer.OrdinalIgnoreCase))
        {
            SelectedProducts.Add(item);
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionCounterText));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(ConfirmButtonText));
        OnPropertyChanged(nameof(SelectionCounterBackground));
        OnPropertyChanged(nameof(SelectionCounterForeground));
        OnPropertyChanged(nameof(SelectionCounterBorder));
        OnPropertyChanged(nameof(PageSelectionState));
        OnPropertyChanged(nameof(IsPageSelected));
    }

    private void UpdatePageNumbers()
    {
        PageNumbers.Clear();

        if (TotalPages <= 0 || TotalCount == 0)
        {
            TargetPageInput = Page.ToString();
            NotifyPaginationState();
            return;
        }

        if (Page <= 0)
        {
            Page = 1;
        }
        if (Page > TotalPages)
        {
            Page = TotalPages;
        }

        var startPage = Math.Max(1, Page - 2);
        var endPage = Math.Min(TotalPages, Page + 2);
        for (var pageNumber = startPage; pageNumber <= endPage; pageNumber++)
        {
            PageNumbers.Add(new PageNumberItem
            {
                PageNumber = pageNumber,
                IsActive = pageNumber == Page
            });
        }

        TargetPageInput = Page.ToString();
        NotifyPaginationState();
    }

    private void NotifyPaginationState()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(CanGoFirst));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoLast));
        OnPropertyChanged(nameof(PageSelectionState));
        OnPropertyChanged(nameof(IsPageSelected));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _categoryAddCts?.Cancel();
            _categoryAddCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        _loadCts = null;
        _categoryAddCts = null;
        GC.SuppressFinalize(this);
    }
}
