using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Core.Helpers;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public enum InvoiceProductResolutionMode
{
    LinkExisting,
    CreateNew
}

/// <summary>
/// Modal híbrido de resolución de productos desde una línea de factura sin coincidencia:
/// Modo A — Vincular con Producto Existente (buscador predictivo por Nombre, SKU o Código de Barras local + aprendizaje de alias).
/// Modo B — Crear Producto Nuevo (captura obligatoria del código de barras universal EAN/UPC).
/// </summary>
public partial class CreateInvoiceProductDialogViewModel : ObservableValidator, IDisposable
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<ProductDto>>>? _searchProductsAsync;
    private CancellationTokenSource? _searchCts;

    public Action<bool>? RequestClose;

    [ObservableProperty]
    private InvoiceProductResolutionMode _resolutionMode;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ProductDto> _searchResults = new();

    [ObservableProperty]
    private ObservableCollection<SelectableProductItemViewModel> _selectableSearchResults = new();

    [ObservableProperty]
    private ProductDto? _selectedProduct;

    public ObservableCollection<ProductDto> SelectedProducts { get; } = new();

    public ObservableCollection<SelectedProductMarginViewModel> SelectedProductMargins { get; } = new();

    private bool _suppressSelectionSync;

    [ObservableProperty]
    private string _bulkMarginRetailText = string.Empty;

    [ObservableProperty]
    private string _bulkMarginWholesaleText = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    [Required(ErrorMessage = "El código de barras es obligatorio.")]
    [RegularExpression(@"^\d{8,14}$", ErrorMessage = "El código de barras debe tener entre 8 y 14 dígitos numéricos.")]
    private string _barcode = string.Empty;

    [ObservableProperty]
    [Required(ErrorMessage = "El nombre del producto es obligatorio.")]
    [MaxLength(100, ErrorMessage = "El nombre del producto no puede superar los 100 caracteres.")]
    private string _name = string.Empty;

    /// <summary>Contexto de solo lectura: nombre original de la línea de factura.</summary>
    public string ProductName { get; }

    /// <summary>Costo unitario base en USD de la línea de factura para el cálculo reactivo de precios sugeridos.</summary>
    public decimal LineUnitCostUSD { get; }

    /// <summary>Etiqueta formateada del costo base unitario para el encabezado del modal.</summary>
    public string LineUnitCostHeaderText => $"Costo Base Unitario: ${LineUnitCostUSD:N2}";

    /// <summary>Contexto de solo lectura: costo normalizado en USD y, para facturas Bs.S, costo de la factura.</summary>
    public string CostInfo { get; }

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool IsLinkExistingMode
    {
        get => ResolutionMode == InvoiceProductResolutionMode.LinkExisting;
        set
        {
            if (value && ResolutionMode != InvoiceProductResolutionMode.LinkExisting)
            {
                ResolutionMode = InvoiceProductResolutionMode.LinkExisting;
            }
        }
    }

    public bool IsCreateNewMode
    {
        get => ResolutionMode == InvoiceProductResolutionMode.CreateNew;
        set
        {
            if (value && ResolutionMode != InvoiceProductResolutionMode.CreateNew)
            {
                ResolutionMode = InvoiceProductResolutionMode.CreateNew;
            }
        }
    }

    public bool HasSelectedProduct => SelectedProducts.Count > 0 || SelectedProduct is not null;

    public bool HasMultipleSelectedProducts => SelectedProducts.Count > 1;

    public bool HasAnyMarginCoherenceWarning =>
        SelectedProductMargins.Any(item => item.HasMarginCoherenceWarning);

    public string MarginCoherenceWarningSummary => HasAnyMarginCoherenceWarning
        ? "Advertencia comercial: uno o más productos tienen un % Margen Mayor superior al % Margen Detal."
        : string.Empty;

    public string SelectedProductSummary
    {
        get
        {
            if (SelectedProducts.Count > 1)
            {
                return $"{SelectedProducts.Count} productos seleccionados: {string.Join(", ", SelectedProducts.Select(product => product.Name))}";
            }

            var current = SelectedProducts.FirstOrDefault() ?? SelectedProduct;
            return current is null
                ? "Seleccione uno o más productos de la lista para asociarlos con esta línea."
                : $"{current.Name} · SKU: {current.SKU} · Costo anterior: ${current.CostPriceUSD:N2} · Stock: {current.StockQuantity:N3}";
        }
    }

    public string PrimaryActionLabel => IsLinkExistingMode
        ? (SelectedProducts.Count > 1 ? $"Vincular ({SelectedProducts.Count}) Productos" : "Vincular Producto")
        : "Crear Producto";

    public CreateInvoiceProductDialogViewModel(
        SupplierInvoiceLineViewModel line,
        bool isBsSCurrency,
        Func<string, CancellationToken, Task<IReadOnlyList<ProductDto>>>? searchProductsAsync = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        _searchProductsAsync = searchProductsAsync;
        _resolutionMode = searchProductsAsync is not null
            ? InvoiceProductResolutionMode.LinkExisting
            : InvoiceProductResolutionMode.CreateNew;

        var effectiveName = !string.IsNullOrWhiteSpace(line.EditName) ? line.EditName : line.Name;
        ProductName = effectiveName ?? string.Empty;
        Name = ProductName.Trim();
        LineUnitCostUSD = line.UnitCostUSD > 0m ? line.UnitCostUSD : line.EditUnitCostDocument;
        CostInfo = isBsSCurrency
            ? $"Costo normalizado: ${line.UnitCostUSD:N2} · Costo factura: Bs.S {line.EditUnitCostDocument:N2} · Cantidad: {line.EditQuantity:N3}"
            : $"Costo normalizado: ${line.UnitCostUSD:N2} · Cantidad: {line.EditQuantity:N3}";

        SelectedProducts.CollectionChanged += (_, _) => NotifySelectionChanged();
    }

    public IReadOnlyList<ProductDto> GetEffectiveSelectedProducts()
    {
        if (SelectedProducts.Count > 0)
        {
            return SelectedProducts.ToList();
        }

        return SelectedProduct is not null
            ? new[] { SelectedProduct }
            : Array.Empty<ProductDto>();
    }

    public IReadOnlyList<LinkedProductMarginOverrideDto> GetSelectedMarginOverrides()
    {
        SyncSelectedProductMargins();
        return SelectedProductMargins
            .Select(item => new LinkedProductMarginOverrideDto(
                item.ProductId,
                item.MarginRetail,
                item.MarginWholesale))
            .ToList();
    }

    private bool CanSave => !IsSaving;

    partial void OnIsSavingChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnResolutionModeChanged(InvoiceProductResolutionMode value)
    {
        ErrorMessage = string.Empty;
        ClearErrors();
        OnPropertyChanged(nameof(IsLinkExistingMode));
        OnPropertyChanged(nameof(IsCreateNewMode));
        OnPropertyChanged(nameof(PrimaryActionLabel));
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && ResolutionMode != InvoiceProductResolutionMode.LinkExisting)
        {
            ResolutionMode = InvoiceProductResolutionMode.LinkExisting;
        }

        SearchCatalogAsync().SafeFireAndForget(nameof(SearchCatalogAsync));
    }

    partial void OnSelectedProductChanged(ProductDto? value)
    {
        if (value is not null && ResolutionMode != InvoiceProductResolutionMode.LinkExisting)
        {
            ResolutionMode = InvoiceProductResolutionMode.LinkExisting;
        }

        if (value is not null)
        {
            ErrorMessage = string.Empty;
            if (!_suppressSelectionSync && SelectedProducts.All(existing => existing.Id != value.Id))
            {
                SelectedProducts.Add(value);
                SyncSelectableItems();
            }
        }

        NotifySelectionChanged();
    }

    partial void OnBarcodeChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && ResolutionMode != InvoiceProductResolutionMode.CreateNew)
        {
            ResolutionMode = InvoiceProductResolutionMode.CreateNew;
        }
    }

    [RelayCommand]
    public void ToggleProductSelection(ProductDto? product)
    {
        if (product is null)
        {
            return;
        }

        if (ResolutionMode != InvoiceProductResolutionMode.LinkExisting)
        {
            ResolutionMode = InvoiceProductResolutionMode.LinkExisting;
        }

        var existing = SelectedProducts.FirstOrDefault(candidate => candidate.Id == product.Id);
        if (existing is not null)
        {
            SelectedProducts.Remove(existing);
            if (SelectedProduct?.Id == product.Id)
            {
                _suppressSelectionSync = true;
                try
                {
                    SelectedProduct = SelectedProducts.FirstOrDefault();
                }
                finally
                {
                    _suppressSelectionSync = false;
                }
            }
        }
        else
        {
            ErrorMessage = string.Empty;
            SelectedProducts.Add(product);
            if (SelectedProduct is null)
            {
                _suppressSelectionSync = true;
                try
                {
                    SelectedProduct = product;
                }
                finally
                {
                    _suppressSelectionSync = false;
                }
            }
        }

        SyncSelectableItems();
        NotifySelectionChanged();
    }

    [RelayCommand]
    public void RemoveSelectedProduct(ProductDto? product)
    {
        if (product is null)
        {
            return;
        }

        var existing = SelectedProducts.FirstOrDefault(candidate => candidate.Id == product.Id);
        if (existing is null)
        {
            return;
        }

        SelectedProducts.Remove(existing);
        if (SelectedProduct?.Id == product.Id)
        {
            _suppressSelectionSync = true;
            try
            {
                SelectedProduct = SelectedProducts.FirstOrDefault();
            }
            finally
            {
                _suppressSelectionSync = false;
            }
        }

        SyncSelectableItems();
        NotifySelectionChanged();
    }

    private void OnSelectableItemToggled(ProductDto product, bool isSelected)
    {
        if (_suppressSelectionSync)
        {
            return;
        }

        var alreadySelected = SelectedProducts.Any(candidate => candidate.Id == product.Id);
        if (isSelected != alreadySelected)
        {
            ToggleProductSelection(product);
        }
    }

    private void SyncSelectableItems()
    {
        var selectedIds = SelectedProducts.Select(product => product.Id).ToHashSet();
        _suppressSelectionSync = true;
        try
        {
            foreach (var item in SelectableSearchResults)
            {
                item.IsSelected = selectedIds.Contains(item.Product.Id);
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    [RelayCommand]
    public void ApplyBulkMargins()
    {
        SyncSelectedProductMargins();
        if (SelectedProductMargins.Count == 0)
        {
            return;
        }

        var hasCustomRetail = SupplierInvoiceLineViewModel.TryParseFlexibleDecimal(BulkMarginRetailText, out var parsedRetail);
        var hasCustomWholesale = SupplierInvoiceLineViewModel.TryParseFlexibleDecimal(BulkMarginWholesaleText, out var parsedWholesale);

        var targetRetail = hasCustomRetail
            ? parsedRetail
            : SelectedProductMargins[0].MarginRetail;
        var targetWholesale = hasCustomWholesale
            ? parsedWholesale
            : SelectedProductMargins[0].MarginWholesale;

        foreach (var item in SelectedProductMargins)
        {
            item.MarginRetail = targetRetail;
            item.MarginWholesale = targetWholesale;
        }

        NotifyMarginsChanged();
    }

    [RelayCommand]
    public void CopyMarginsToAll(SelectedProductMarginViewModel? source)
    {
        SyncSelectedProductMargins();
        var template = source ?? SelectedProductMargins.FirstOrDefault();
        if (template is null)
        {
            return;
        }

        foreach (var item in SelectedProductMargins)
        {
            item.MarginRetail = template.MarginRetail;
            item.MarginWholesale = template.MarginWholesale;
        }

        NotifyMarginsChanged();
    }

    private void SyncSelectedProductMargins()
    {
        var effectiveProducts = GetEffectiveSelectedProducts();
        var existingById = SelectedProductMargins.ToDictionary(item => item.ProductId);
        var targetIds = effectiveProducts.Select(product => product.Id).ToHashSet();

        for (var index = SelectedProductMargins.Count - 1; index >= 0; index--)
        {
            if (!targetIds.Contains(SelectedProductMargins[index].ProductId))
            {
                SelectedProductMargins.RemoveAt(index);
            }
        }

        for (var index = 0; index < effectiveProducts.Count; index++)
        {
            var product = effectiveProducts[index];
            if (!existingById.TryGetValue(product.Id, out var marginVm))
            {
                marginVm = new SelectedProductMarginViewModel(product, LineUnitCostUSD, NotifyMarginsChanged);
                if (index < SelectedProductMargins.Count)
                {
                    SelectedProductMargins.Insert(index, marginVm);
                }
                else
                {
                    SelectedProductMargins.Add(marginVm);
                }
            }
        }
    }

    private void NotifyMarginsChanged()
    {
        OnPropertyChanged(nameof(HasAnyMarginCoherenceWarning));
        OnPropertyChanged(nameof(MarginCoherenceWarningSummary));
    }

    private void NotifySelectionChanged()
    {
        SyncSelectedProductMargins();
        OnPropertyChanged(nameof(HasSelectedProduct));
        OnPropertyChanged(nameof(HasMultipleSelectedProducts));
        OnPropertyChanged(nameof(SelectedProductSummary));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        NotifyMarginsChanged();
    }

    [RelayCommand]
    public async Task SearchCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (_searchProductsAsync is null)
        {
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _searchCts.Token;

        var query = SearchQuery?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResults.Clear();
            SelectableSearchResults.Clear();
            IsSearching = false;
            return;
        }

        IsSearching = true;
        ErrorMessage = string.Empty;
        try
        {
            var results = await _searchProductsAsync(query, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            SearchResults = new ObservableCollection<ProductDto>(results);
            var selectedIds = SelectedProducts.Select(product => product.Id).ToHashSet();
            SelectableSearchResults = new ObservableCollection<SelectableProductItemViewModel>(
                results.Select(product => new SelectableProductItemViewModel(
                    product,
                    selectedIds.Contains(product.Id),
                    OnSelectableItemToggled)));
            if (SelectedProduct is not null)
            {
                _suppressSelectionSync = true;
                try
                {
                    SelectedProduct = SearchResults.FirstOrDefault(product => product.Id == SelectedProduct.Id)
                        ?? SelectedProducts.FirstOrDefault();
                }
                finally
                {
                    _suppressSelectionSync = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Búsqueda previa cancelada por nueva entrada del usuario.
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                ErrorMessage = $"No se pudo buscar en el catálogo: {exception.Message}";
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSearching = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (IsSaving)
        {
            return;
        }

        ErrorMessage = string.Empty;
        ClearErrors();

        if (IsLinkExistingMode)
        {
            if (SelectedProducts.Count == 0 && SelectedProduct is null)
            {
                ErrorMessage = "Seleccione un producto existente del catálogo para vincular.";
                return;
            }

            RequestClose?.Invoke(true);
            return;
        }

        Barcode = Barcode.Trim();
        Name = Name.Trim();
        ValidateAllProperties();
        if (HasErrors)
        {
            ErrorMessage = GetErrors(null).FirstOrDefault()?.ErrorMessage ?? "Revise los datos del producto.";
            return;
        }

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        RequestClose = null;
    }
}

public sealed partial class SelectableProductItemViewModel : ObservableObject
{
    private readonly Action<ProductDto, bool> _onSelectionChanged;

    public ProductDto Product { get; }

    public int Id => Product.Id;
    public string Name => Product.Name;
    public string SKU => Product.SKU;
    public decimal CostPriceUSD => Product.CostPriceUSD;
    public decimal PriceRetailUSD => Product.PriceRetailUSD;
    public decimal StockQuantity => Product.StockQuantity;

    [ObservableProperty]
    private bool _isSelected;

    public SelectableProductItemViewModel(
        ProductDto product,
        bool isSelected,
        Action<ProductDto, bool> onSelectionChanged)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(onSelectionChanged);

        Product = product;
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged(Product, value);
}

public sealed class SelectedProductMarginViewModel : ObservableObject
{
    private readonly Action? _onMarginChanged;
    private decimal _marginRetail;
    private string _marginRetailText;
    private decimal _marginWholesale;
    private string _marginWholesaleText;

    public ProductDto Product { get; }

    public int ProductId => Product.Id;
    public string Name => Product.Name;
    public string SKU => Product.SKU;
    public decimal OldCostPriceUSD => Product.CostPriceUSD;
    public decimal StockQuantity => Product.StockQuantity;
    public decimal LineUnitCostUSD { get; }

    public decimal MarginRetail
    {
        get => _marginRetail;
        set
        {
            var normalized = decimal.Round(Math.Max(0m, value), 2, MidpointRounding.AwayFromZero);
            var formatted = SupplierInvoiceLineViewModel.FormatEditableDecimal(normalized);
            if (_marginRetail == normalized && string.Equals(_marginRetailText, formatted, StringComparison.Ordinal))
            {
                return;
            }

            _marginRetail = normalized;
            _marginRetailText = formatted;
            OnPropertyChanged(nameof(MarginRetail));
            OnPropertyChanged(nameof(MarginRetailText));
            OnPropertyChanged(nameof(SuggestedRetailPriceUSD));
            OnPropertyChanged(nameof(HasMarginCoherenceWarning));
            OnPropertyChanged(nameof(MarginCoherenceWarningMessage));
            _onMarginChanged?.Invoke();
        }
    }

    public string MarginRetailText
    {
        get => _marginRetailText;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_marginRetailText, next, StringComparison.Ordinal))
            {
                return;
            }

            _marginRetailText = next;
            OnPropertyChanged(nameof(MarginRetailText));

            if (SupplierInvoiceLineViewModel.TryParseFlexibleDecimal(next, out var parsed))
            {
                var normalized = decimal.Round(Math.Max(0m, parsed), 2, MidpointRounding.AwayFromZero);
                if (_marginRetail != normalized)
                {
                    _marginRetail = normalized;
                    OnPropertyChanged(nameof(MarginRetail));
                    OnPropertyChanged(nameof(SuggestedRetailPriceUSD));
                    OnPropertyChanged(nameof(HasMarginCoherenceWarning));
                    OnPropertyChanged(nameof(MarginCoherenceWarningMessage));
                    _onMarginChanged?.Invoke();
                }
            }
        }
    }

    public decimal MarginWholesale
    {
        get => _marginWholesale;
        set
        {
            var normalized = decimal.Round(Math.Max(0m, value), 2, MidpointRounding.AwayFromZero);
            var formatted = SupplierInvoiceLineViewModel.FormatEditableDecimal(normalized);
            if (_marginWholesale == normalized && string.Equals(_marginWholesaleText, formatted, StringComparison.Ordinal))
            {
                return;
            }

            _marginWholesale = normalized;
            _marginWholesaleText = formatted;
            OnPropertyChanged(nameof(MarginWholesale));
            OnPropertyChanged(nameof(MarginWholesaleText));
            OnPropertyChanged(nameof(SuggestedWholesalePriceUSD));
            OnPropertyChanged(nameof(HasMarginCoherenceWarning));
            OnPropertyChanged(nameof(MarginCoherenceWarningMessage));
            _onMarginChanged?.Invoke();
        }
    }

    public string MarginWholesaleText
    {
        get => _marginWholesaleText;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_marginWholesaleText, next, StringComparison.Ordinal))
            {
                return;
            }

            _marginWholesaleText = next;
            OnPropertyChanged(nameof(MarginWholesaleText));

            if (SupplierInvoiceLineViewModel.TryParseFlexibleDecimal(next, out var parsed))
            {
                var normalized = decimal.Round(Math.Max(0m, parsed), 2, MidpointRounding.AwayFromZero);
                if (_marginWholesale != normalized)
                {
                    _marginWholesale = normalized;
                    OnPropertyChanged(nameof(MarginWholesale));
                    OnPropertyChanged(nameof(SuggestedWholesalePriceUSD));
                    OnPropertyChanged(nameof(HasMarginCoherenceWarning));
                    OnPropertyChanged(nameof(MarginCoherenceWarningMessage));
                    _onMarginChanged?.Invoke();
                }
            }
        }
    }

    public decimal SuggestedRetailPriceUSD =>
        PricingCalculator.RoundPriceUp(LineUnitCostUSD * (1m + (_marginRetail / 100m)));

    public decimal SuggestedWholesalePriceUSD =>
        PricingCalculator.RoundPriceUp(LineUnitCostUSD * (1m + (_marginWholesale / 100m)));

    public bool HasMarginCoherenceWarning => _marginWholesale > _marginRetail;

    public string MarginCoherenceWarningMessage => HasMarginCoherenceWarning
        ? "Advertencia comercial: el % Margen Mayor supera al % Margen Detal."
        : string.Empty;

    public SelectedProductMarginViewModel(
        ProductDto product,
        decimal lineUnitCostUsd,
        Action? onMarginChanged = null)
    {
        ArgumentNullException.ThrowIfNull(product);

        Product = product;
        LineUnitCostUSD = Math.Max(0m, lineUnitCostUsd);
        _onMarginChanged = onMarginChanged;

        var initialRetail = product.ProfitMarginRetail == 0m && product.ProfitPercentage > 0m
            ? product.ProfitPercentage
            : product.ProfitMarginRetail;
        var initialWholesale = product.HasWholesale || product.ProfitMarginWholesale > 0m
            ? product.ProfitMarginWholesale
            : initialRetail;

        _marginRetail = decimal.Round(Math.Max(0m, initialRetail), 2, MidpointRounding.AwayFromZero);
        _marginRetailText = SupplierInvoiceLineViewModel.FormatEditableDecimal(_marginRetail);
        _marginWholesale = decimal.Round(Math.Max(0m, initialWholesale), 2, MidpointRounding.AwayFromZero);
        _marginWholesaleText = SupplierInvoiceLineViewModel.FormatEditableDecimal(_marginWholesale);
    }
}


