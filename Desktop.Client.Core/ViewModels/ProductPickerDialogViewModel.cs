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
    private bool _initialized;
    private bool _suppressSelectionCallback;
    private bool _suppressCategoryReload;
    private int _disposed;

    public event Action<bool>? RequestClose;

    public ObservableCollection<ProductPickerCategoryOption> Categories { get; } = new();
    public ObservableCollection<ProductPickerRowViewModel> Results { get; } = new();
    public ObservableCollection<ProductPickerSelection> SelectedProducts { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ProductPickerCategoryOption? _selectedCategory;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private int _totalCount;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasResults => Results.Count > 0;
    public bool HasSelection => SelectedProducts.Count > 0;
    public int SelectedCount => SelectedProducts.Count;
    public string SelectionCounterText => $"{SelectedCount} / {MaxSelection}";
    public string PageText => $"Página {Page} de {TotalPages}";
    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
    public bool CanConfirm => SelectedCount > 0;

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
        if (_initialized && !_suppressCategoryReload && _disposed == 0)
        {
            LoadPageAsync(1).SafeFireAndForget("ProductPickerDialogViewModel.Category");
        }
    }

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

            NotifyPageState();
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
    }

    private void NotifyPageState()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
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
        }
        catch (ObjectDisposedException) { }

        _loadCts = null;
        GC.SuppressFinalize(this);
    }
}
