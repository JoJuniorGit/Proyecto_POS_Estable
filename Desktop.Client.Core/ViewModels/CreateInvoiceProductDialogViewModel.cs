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
    private ProductDto? _selectedProduct;

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

    public bool HasSelectedProduct => SelectedProduct is not null;

    public string SelectedProductSummary => SelectedProduct is null
        ? "Seleccione un producto de la lista para asociarlo con esta línea."
        : $"{SelectedProduct.Name} · SKU: {SelectedProduct.SKU} · Costo anterior: ${SelectedProduct.CostPriceUSD:N2} · Stock: {SelectedProduct.StockQuantity:N3}";

    public string PrimaryActionLabel => IsLinkExistingMode ? "Vincular Producto" : "Crear Producto";

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
        CostInfo = isBsSCurrency
            ? $"Costo normalizado: ${line.UnitCostUSD:N2} · Costo factura: Bs.S {line.EditUnitCostDocument:N2} · Cantidad: {line.EditQuantity:N3}"
            : $"Costo normalizado: ${line.UnitCostUSD:N2} · Cantidad: {line.EditQuantity:N3}";
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
        }

        OnPropertyChanged(nameof(HasSelectedProduct));
        OnPropertyChanged(nameof(SelectedProductSummary));
    }

    partial void OnBarcodeChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && ResolutionMode != InvoiceProductResolutionMode.CreateNew)
        {
            ResolutionMode = InvoiceProductResolutionMode.CreateNew;
        }
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
            if (SelectedProduct is not null)
            {
                SelectedProduct = SearchResults.FirstOrDefault(product => product.Id == SelectedProduct.Id);
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
            if (SelectedProduct is null)
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

