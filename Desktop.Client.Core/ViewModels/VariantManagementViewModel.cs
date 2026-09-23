using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class VariantManagementViewModel : ObservableObject, IDisposable
{
    private readonly IProductService _productService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly IDialogService _dialogService;
    private System.Threading.CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private ProductDto _parentProduct;

    [ObservableProperty]
    private ObservableCollection<VariantItemViewModel> _variants = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLinkingPanelOpen;

    [ObservableProperty]
    private string _searchCatalogText = string.Empty;

    [ObservableProperty]
    private bool _isSearchingCatalog;

    [ObservableProperty]
    private ObservableCollection<CandidateProductItemViewModel> _candidateProducts = new();

    [ObservableProperty]
    private bool _hasSelectedCandidates;

    public bool IsStockShared => ParentProduct.IsStockShared;
    public bool HasIndependentPricing => ParentProduct.HasIndependentPricing;

    public string HeaderInfo => $"{ParentProduct.Name} (SKU: {ParentProduct.SKU})";
    public string StockModeDescription => IsStockShared 
        ? "Stock Compartido (Pool centralizado en el producto padre)" 
        : "Stock Individual por cada variante";
    public string PricingModeDescription => HasIndependentPricing 
        ? "Precios Independientes (Cada variante define sus precios)" 
        : "Precios Heredados (Sincronizados automáticamente desde el padre)";

    public Action<bool>? RequestClose;

    public VariantManagementViewModel(
        IProductService productService,
        IExchangeRateService exchangeRateService,
        IDialogService dialogService,
        ProductDto parentProduct)
    {
        _productService = productService;
        _exchangeRateService = exchangeRateService;
        _dialogService = dialogService;
        _parentProduct = parentProduct;

        LoadVariantsAsync().SafeFireAndForget("VariantManagementViewModel.LoadVariants");
    }

    [RelayCommand]
    public async Task LoadVariantsAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Cargando variantes...";
            var list = await _productService.GetVariantsAsync(ParentProduct.Id);
            Variants.Clear();
            foreach (var dto in list)
            {
                Variants.Add(new VariantItemViewModel(dto, HasIndependentPricing, IsStockShared, _exchangeRateService.CurrentRate));
            }
            StatusMessage = $"{Variants.Count} variantes cargadas.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Error al cargar variantes.";
            _dialogService.ShowError("Error", $"No se pudieron cargar las variantes: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task SaveBatchAsync()
    {
        var modified = Variants.Where(v => v.IsModified).ToList();
        if (modified.Count == 0)
        {
            _dialogService.ShowInfo("Información", "No hay cambios pendientes por guardar.");
            return;
        }

        try
        {
            IsSaving = true;
            StatusMessage = "Guardando cambios...";

            foreach (var item in modified)
            {
                var prod = await _productService.GetByIdAsync(item.Id);
                if (prod == null) continue;

                var dto = ProductClientMapping.ToUpdateProductDto(prod);
                dto.Name = item.Name.Trim();
                dto.IsActive = item.IsActive;

                if (HasIndependentPricing)
                {
                    dto.CostPriceUSD = item.CostPriceUSD;
                    dto.ProfitMarginRetail = item.ProfitMarginRetail;
                    dto.PriceRetailUSD = item.PriceRetailUSD;
                    dto.PriceUSD = item.PriceRetailUSD;
                    dto.HasWholesale = item.HasWholesale;
                    dto.ProfitMarginWholesale = item.ProfitMarginWholesale;
                    dto.PriceWholesaleUSD = item.PriceWholesaleUSD;
                    dto.MinWholesaleQuantity = item.MinWholesaleQuantity;
                }

                if (IsStockShared)
                {
                    dto.ConversionFactor = item.ConversionFactor > 0 ? item.ConversionFactor : 1.0000m;
                }

                await _productService.UpdateAsync(dto);
                item.IsModified = false;
            }

            StatusMessage = "Cambios guardados correctamente.";
            _dialogService.ShowSuccessDialog("Las variantes fueron actualizadas exitosamente.");
            await LoadVariantsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Conflicto o error al guardar.";
            bool reload = _dialogService.ShowConfirm("Conflicto de Concurrencia", 
                $"Ocurrió un error o los datos fueron modificados por otro usuario concurrentemente:\n{ex.Message}\n\n¿Desea recargar la lista de variantes?");
            if (reload)
            {
                await LoadVariantsAsync();
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    public async Task AddVariantAsync()
    {
        string? name = await _dialogService.ShowTextInputAsync("Nueva Variante", "Nombre de la variante (ej. Sabor Fresa, Talla L):");
        if (string.IsNullOrWhiteSpace(name)) return;

        string? sku = await _dialogService.ShowTextInputAsync("SKU de Variante", "Código SKU numérico para la variante:");
        if (string.IsNullOrWhiteSpace(sku)) return;

        try
        {
            IsSaving = true;
            var newProd = new CreateProductDto
            {
                Name = name.Trim(),
                SKU = sku.Trim(),
                ParentProductId = ParentProduct.Id,
                IsGroupHeader = false,
                IsStockShared = false,
                HasIndependentPricing = false,
                IsActive = true
            };

            if (!HasIndependentPricing)
            {
                newProd.CostPriceUSD = ParentProduct.CostPriceUSD;
                newProd.ProfitMarginRetail = ParentProduct.ProfitMarginRetail;
                newProd.PriceRetailUSD = ParentProduct.PriceRetailUSD;
                newProd.PriceUSD = ParentProduct.PriceRetailUSD;
                newProd.HasWholesale = ParentProduct.HasWholesale;
                newProd.ProfitMarginWholesale = ParentProduct.ProfitMarginWholesale;
                newProd.PriceWholesaleUSD = ParentProduct.PriceWholesaleUSD;
                newProd.MinWholesaleQuantity = ParentProduct.MinWholesaleQuantity;
                newProd.IsFractional = ParentProduct.IsFractional;
                newProd.UnitOfMeasure = ParentProduct.UnitOfMeasure;
            }
            else
            {
                newProd.CostPriceUSD = ParentProduct.CostPriceUSD;
                newProd.ProfitMarginRetail = ParentProduct.ProfitMarginRetail;
                newProd.PriceRetailUSD = ParentProduct.PriceRetailUSD;
                newProd.PriceUSD = ParentProduct.PriceRetailUSD;
            }

            newProd.StockQuantity = 0m;
            newProd.LowStockThreshold = IsStockShared ? 0m : 5m;

            await _productService.CreateAsync(newProd);
            _dialogService.ShowSuccessDialog($"Variante '{newProd.Name}' agregada con éxito.");
            await LoadVariantsAsync();
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error al agregar variante", ex.Message);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    public void Close()
    {
        RequestClose?.Invoke(true);
    }

    public void Dispose()
    {
        var oldCts = System.Threading.Interlocked.Exchange(ref _searchCts, null);
        try { oldCts?.Cancel(); oldCts?.Dispose(); } catch (ObjectDisposedException) { }
    }
}

public partial class CandidateProductItemViewModel : ObservableObject
{
    public int Id { get; }
    public string Name { get; }
    public string SKU { get; }
    public decimal PriceUSD { get; }
    public decimal StockQuantity { get; }

    [ObservableProperty]
    private bool _isSelected;

    public Action? SelectionChanged { get; set; }

    public CandidateProductItemViewModel(ProductDto dto)
    {
        Id = dto.Id;
        Name = dto.Name;
        SKU = dto.SKU;
        PriceUSD = dto.PriceRetailUSD > 0 ? dto.PriceRetailUSD : dto.PriceUSD;
        StockQuantity = dto.StockQuantity;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke();
    }
}

public partial class VariantItemViewModel : ObservableObject
{
    public int Id { get; set; }
    public string SKU { get; set; }
    public bool CanEditPrices { get; }
    public bool CanEditStock { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private decimal _costPriceUSD;

    [ObservableProperty]
    private decimal _profitMarginRetail;

    [ObservableProperty]
    private decimal _priceRetailUSD;

    [ObservableProperty]
    private decimal _priceRetailBsS;

    [ObservableProperty]
    private bool _hasWholesale;

    [ObservableProperty]
    private decimal _profitMarginWholesale;

    [ObservableProperty]
    private decimal _priceWholesaleUSD;

    [ObservableProperty]
    private decimal _minWholesaleQuantity;

    [ObservableProperty]
    private decimal _stockQuantity;

    [ObservableProperty]
    private decimal _conversionFactor;

    [ObservableProperty]
    private bool _isModified;

    public bool CanEditConversionFactor { get; }

    private readonly decimal _exchangeRate;

    public VariantItemViewModel(ProductDto dto, bool hasIndependentPricing, bool isStockShared, decimal exchangeRate)
    {
        Id = dto.Id;
        SKU = dto.SKU;
        _name = dto.Name;
        _isActive = dto.IsActive;
        _costPriceUSD = dto.CostPriceUSD > 0 ? dto.CostPriceUSD : dto.Cost;
        _profitMarginRetail = dto.ProfitMarginRetail > 0 ? dto.ProfitMarginRetail : dto.ProfitPercentage;
        _priceRetailUSD = dto.PriceRetailUSD > 0 ? dto.PriceRetailUSD : dto.PriceUSD;
        _hasWholesale = dto.HasWholesale;
        _profitMarginWholesale = dto.ProfitMarginWholesale;
        _priceWholesaleUSD = dto.PriceWholesaleUSD;
        _minWholesaleQuantity = dto.MinWholesaleQuantity > 0 ? dto.MinWholesaleQuantity : 6m;
        _stockQuantity = dto.StockQuantity;
        _conversionFactor = dto.ConversionFactor > 0 ? dto.ConversionFactor : 1.0000m;

        CanEditPrices = hasIndependentPricing;
        CanEditStock = !isStockShared;
        CanEditConversionFactor = isStockShared;
        _exchangeRate = exchangeRate;

        _priceRetailBsS = (_exchangeRate > 0 && _priceRetailUSD > 0)
            ? Helpers.PricingHelper.ToBsSCeiling(_priceRetailUSD, _exchangeRate)
            : dto.PriceBsS;

        IsModified = false;
    }

    partial void OnNameChanged(string value) => IsModified = true;
    partial void OnIsActiveChanged(bool value) => IsModified = true;
    partial void OnConversionFactorChanged(decimal value) => IsModified = true;

    partial void OnCostPriceUSDChanged(decimal value)
    {
        IsModified = true;
        RecalculatePrices();
    }

    partial void OnProfitMarginRetailChanged(decimal value)
    {
        IsModified = true;
        RecalculatePrices();
    }

    partial void OnPriceRetailUSDChanged(decimal value)
    {
        IsModified = true;
        PriceRetailBsS = (_exchangeRate > 0 && value > 0)
            ? Helpers.PricingHelper.ToBsSCeiling(value, _exchangeRate)
            : 0m;
    }

    private void RecalculatePrices()
    {
        if (!CanEditPrices) return;
        if (CostPriceUSD > 0 && ProfitMarginRetail >= 0)
        {
            PriceRetailUSD = Math.Round(CostPriceUSD * (1 + (ProfitMarginRetail / 100m)), 2, MidpointRounding.AwayFromZero);
        }
    }
}
