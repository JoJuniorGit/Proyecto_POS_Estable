using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Core.Logging;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Paso 15 (S5/S6): informe ejecutivo de rotacion y puente al dialogo de edicion masiva del Paso 9.
/// La vista no calcula metricas: consume el reporte server-side y solo selecciona IDs de stock
/// muerto para abrir el motor de precios existente.
/// </summary>
public partial class InventoryHealthViewModel : ObservableObject, IDisposable
{
    private readonly IInventoryRotationService _rotationService;
    private readonly IProductService _productService;
    private readonly ICategoryService? _categoryService;
    private readonly IDialogService? _dialogService;
    private readonly ISettingsService? _settingsService;
    private readonly UserSession? _userSession;
    private readonly IDispatcherInvoker? _dispatcherInvoker;
    private bool _isInitialized;

    public InventoryHealthViewModel(
        IInventoryRotationService rotationService,
        IProductService productService,
        ICategoryService? categoryService = null,
        IDialogService? dialogService = null,
        ISettingsService? settingsService = null,
        UserSession? userSession = null,
        IDispatcherInvoker? dispatcherInvoker = null)
    {
        ArgumentNullException.ThrowIfNull(rotationService);
        ArgumentNullException.ThrowIfNull(productService);

        _rotationService = rotationService;
        _productService = productService;
        _categoryService = categoryService;
        _dialogService = dialogService;
        _settingsService = settingsService;
        _userSession = userSession;
        _dispatcherInvoker = dispatcherInvoker;

        RotationClassOptions = new ObservableCollection<RotationClassFilterOption>
        {
            new(null, "Todas las rotaciones"),
            new(InventoryRotationClass.High, "Alta rotacion"),
            new(InventoryRotationClass.Medium, "Media rotacion"),
            new(InventoryRotationClass.Low, "Baja rotacion"),
            new(InventoryRotationClass.DeadStock, "Stock muerto")
        };
        SelectedRotationClassOption = RotationClassOptions[0];
    }

    public ObservableCollection<InventoryRotationProductDto> Products { get; } = new();
    public ObservableCollection<InventoryRotationCategoryDto> Categories { get; } = new();
    public ObservableCollection<CategoryOption> CategoryFilterOptions { get; } = new();
    public ObservableCollection<RotationClassFilterOption> RotationClassOptions { get; }

    public IReadOnlyList<int> DeadStockDayOptions { get; } = new[] { 45, 60, 90 };
    public IReadOnlyList<int> SalesWindowDayOptions { get; } = new[] { 30, 60, 90, 180 };

    public Action? RequestClose { get; set; }

    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public bool HasAppliedPriceChanges { get; private set; }

    [ObservableProperty]
    private InventoryRotationSummaryDto _summary = new();

    [ObservableProperty]
    private int _salesWindowDays = 90;

    [ObservableProperty]
    private int _deadStockDays = 60;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _pageSize = 50;

    [ObservableProperty]
    private int _totalPages;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private CategoryOption? _selectedCategoryFilter = CategoryOption.AllFilter;

    [ObservableProperty]
    private RotationClassFilterOption? _selectedRotationClassOption;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasDeadStock => Summary.DeadStockCount > 0;
    public bool IsEmpty => !IsLoading && Products.Count == 0;
    public bool CanMutateCatalog => _userSession == null || _userSession.CanMutateCatalog;
    public bool CanApplyPromotion => !IsLoading && HasDeadStock && CanMutateCatalog;
    public bool CanGoPrevious => !IsLoading && CurrentPage > 1;
    public bool CanGoNext => !IsLoading && CurrentPage < TotalPages;

    public string PageSummary => TotalCount == 0
        ? "Sin productos para los filtros seleccionados"
        : $"Pagina {CurrentPage} de {Math.Max(TotalPages, 1)} ({TotalCount} productos)";

    public string GeneratedAtText => Summary.TotalProducts == 0
        ? "Sin datos en el periodo"
        : $"Actualizado {DateTime.Now:dd/MM/yyyy HH:mm}";

    public string FilterSummary =>
        $"Ventana {SalesWindowDays} dias · stock muerto >= {DeadStockDays} dias";

    public Task EnsureLoadedAsync()
    {
        if (_isInitialized)
        {
            return LoadTask;
        }

        _isInitialized = true;
        LoadTask = InitializeAsync();
        return LoadTask;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanApplyPromotion))]
    private async Task ApplyPromotionAsync()
    {
        if (_dialogService == null)
        {
            return;
        }

        if (!CanMutateCatalog)
        {
            _dialogService.ShowWarning(
                "Permisos insuficientes",
                "La liquidacion masiva requiere permisos de edicion del catalogo.");
            return;
        }

        DeadStockSelectionDto selection;
        try
        {
            selection = await _rotationService.GetDeadStockProductIdsAsync(BuildFilter());
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("No se pudo preparar la promocion", ex.Message);
            return;
        }

        if (selection.TotalCount == 0)
        {
            _dialogService.ShowInfo(
                "Sin stock muerto",
                "No hay productos que superen el umbral configurado.");
            return;
        }

        if (selection.Truncated)
        {
            _dialogService.ShowWarning(
                "Demasiados productos",
                $"La seleccion incluye {selection.TotalCount} productos y la edicion masiva acepta hasta " +
                $"{InventoryRotationCalculatorLimit} por operacion. Refine el filtro de categoria o aumente el umbral.");
            return;
        }

        var dialogVm = new BulkPriceEditDialogViewModel(
            _productService,
            _dialogService,
            new BulkPriceSelection(
                selection.ProductIds,
                Filter: null,
                SelectAllFiltered: false,
                SelectedCount: selection.ProductIds.Count),
            await LoadGlobalPricingPoliciesAsync());

        if (!_dialogService.ShowBulkPriceEditDialog(dialogVm))
        {
            return;
        }

        HasAppliedPriceChanges = true;
        _dialogService.ShowSuccessDialog($"Promocion aplicada a {dialogVm.AppliedCount} producto(s).");
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousPageAsync()
    {
        CurrentPage--;
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextPageAsync()
    {
        CurrentPage++;
        await LoadAsync();
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        NotifyStateChanged();

        try
        {
            var report = await _rotationService.GetReportAsync(BuildFilter());
            ApplyReport(report);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            ClientStateLogger.LogError(
                $"Fallo al cargar el reporte de rotacion de inventario: {ex.Message}",
                nameof(InventoryHealthViewModel));
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }

    public InventoryRotationFilterDto BuildFilter() => new()
    {
        SalesWindowDays = SalesWindowDays,
        DeadStockDays = DeadStockDays,
        CategoryId = SelectedCategoryFilter is { Id: > 0 } category ? category.Id : null,
        IncludeCategorySubtree = true,
        SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        RotationClass = SelectedRotationClassOption?.Value,
        SortBy = "daysSinceLastSale",
        Descending = true,
        Page = CurrentPage,
        PageSize = PageSize
    };

    public void Dispose()
    {
    }

    private async Task InitializeAsync()
    {
        await LoadCategoryFilterAsync();
        await LoadAsync();
    }

    private async Task LoadCategoryFilterAsync()
    {
        if (_categoryService == null)
        {
            return;
        }

        try
        {
            var categories = await _categoryService.GetCategoriesAsync();
            var options = CategoryOption.BuildFlat(categories);
            CategoryFilterOptions.Clear();
            CategoryFilterOptions.Add(CategoryOption.AllFilter);
            foreach (var option in options)
            {
                CategoryFilterOptions.Add(option);
            }

            SelectedCategoryFilter = CategoryFilterOptions.FirstOrDefault(
                option => option.Id == SelectedCategoryFilter?.Id) ?? CategoryOption.AllFilter;
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError(
                $"Fallo al cargar categorias del reporte de rotacion: {ex.Message}",
                nameof(InventoryHealthViewModel));
        }
    }

    private void ApplyReport(InventoryRotationReportDto report)
    {
        void Apply()
        {
            Summary = report.Summary;
            CurrentPage = report.Page;
            PageSize = report.PageSize;
            TotalPages = report.TotalPages;
            TotalCount = report.TotalCount;

            Products.Clear();
            foreach (var product in report.Products)
            {
                Products.Add(product);
            }

            Categories.Clear();
            foreach (var category in report.Categories)
            {
                Categories.Add(category);
            }

            OnPropertyChanged(nameof(PageSummary));
            OnPropertyChanged(nameof(GeneratedAtText));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasDeadStock));
            OnPropertyChanged(nameof(CanApplyPromotion));
            OnPropertyChanged(nameof(CanGoPrevious));
            OnPropertyChanged(nameof(CanGoNext));
            ApplyPromotionCommand.NotifyCanExecuteChanged();
            PreviousPageCommand.NotifyCanExecuteChanged();
            NextPageCommand.NotifyCanExecuteChanged();
        }

        if (_dispatcherInvoker != null && !_dispatcherInvoker.CheckAccess())
        {
            _dispatcherInvoker.Invoke(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanApplyPromotion));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        ApplyPromotionCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private async Task<PricingPoliciesDto?> LoadGlobalPricingPoliciesAsync()
    {
        if (_settingsService == null)
        {
            return null;
        }

        try
        {
            return await _settingsService.GetPricingPoliciesAsync();
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError(
                $"Fallo al cargar politicas globales para la promocion: {ex.Message}",
                nameof(InventoryHealthViewModel));
            return null;
        }
    }

    public sealed record RotationClassFilterOption(string? Value, string Label);

    // El limite vive en el contrato del Paso 9; se replica solo como texto de ayuda para no
    // enlazar la vista al proyecto Inventory.Module.
    private const int InventoryRotationCalculatorLimit = 1000;

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnCurrentPageChanged(int value) => OnPropertyChanged(nameof(PageSummary));

    partial void OnTotalPagesChanged(int value) => OnPropertyChanged(nameof(PageSummary));

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(PageSummary));

    partial void OnSalesWindowDaysChanged(int value) => OnPropertyChanged(nameof(FilterSummary));

    partial void OnDeadStockDaysChanged(int value) => OnPropertyChanged(nameof(FilterSummary));
}
