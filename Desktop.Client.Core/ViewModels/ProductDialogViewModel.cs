using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Logging;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class ProductDialogViewModel : ObservableValidator, IDisposable
{
    private readonly IProductService _productService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly System.Windows.Threading.DispatcherTimer _debounceTimer;
    private CancellationTokenSource? _skuCancellationTokenSource;
    private readonly Product? _initialProduct;
    private readonly IDialogService? _dialogService;

    public Action<bool>? RequestClose;
    public Product ResultProduct { get; private set; }
    public UserSession? UserSession { get; }

    [ObservableProperty]
    private string _dialogTitle;

    [ObservableProperty]
    [Required(ErrorMessage = "Product Name is required")]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _sku = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isFractional;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStockDisplay))]
    private Core.Entities.UnitOfMeasureType _unitOfMeasureType = Core.Entities.UnitOfMeasureType.Und;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStockDisplay))]
    [Range(0, double.MaxValue, ErrorMessage = "Stock Quantity must be non-negative")]
    private decimal _stockQuantity;

    public string CurrentStockDisplay => $"{StockQuantity:G29} {UnitOfMeasureType}";

    [ObservableProperty]
    [Range(0, double.MaxValue, ErrorMessage = "Low Stock Threshold must be non-negative")]
    private decimal _lowStockThreshold;

    [ObservableProperty]
    private string _unitOfMeasure = "Unit";

    [ObservableProperty]
    private bool _isService;

    [ObservableProperty]
    private bool _isEditMode;

    public bool IsCreateMode => !IsEditMode;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isSkuVerifying;

    [ObservableProperty]
    private string _skuVerificationMessage = string.Empty;

    [ObservableProperty]
    private bool _isSkuValid = true;

    [ObservableProperty]
    private bool _isLoadingMetadata;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditGroupHeader))]
    [NotifyPropertyChangedFor(nameof(CanSelectParentProduct))]
    private bool _isCashAdvance;

    public ObservableCollection<Core.Entities.UnitOfMeasureType> UnitOfMeasureTypes { get; } = new(Enum.GetValues<Core.Entities.UnitOfMeasureType>());
    public ObservableCollection<string> UnitOfMeasures { get; } = new ObservableCollection<string>();

    public ProductDialogViewModel(IProductService productService, IExchangeRateService exchangeRateService, Product? product = null, UserSession? userSession = null, IDialogService? dialogService = null)
    {
        _productService = productService;
        _exchangeRateService = exchangeRateService;
        _initialProduct = product;
        _dialogService = dialogService;
        UserSession = userSession;

        IsEditMode = product != null;
        DialogTitle = IsEditMode ? "Editar Producto" : "Nuevo Producto";
        ResultProduct = new Product { IsActive = true };

        if (_initialProduct != null)
        {
            IsGroupHeader = _initialProduct.IsGroupHeader;
            IsStockShared = _initialProduct.IsStockShared;
            HasIndependentPricing = _initialProduct.HasIndependentPricing;
            GroupKey = _initialProduct.GroupKey;
            ParentProductId = _initialProduct.ParentProductId;
            Name = _initialProduct.Name;
            Sku = _initialProduct.SKU;
            Description = _initialProduct.Description ?? string.Empty;

            CostPriceUSD = _initialProduct.CostPriceUSD > 0 ? _initialProduct.CostPriceUSD : _initialProduct.Cost;
            ProfitMarginRetail = _initialProduct.ProfitMarginRetail > 0 ? _initialProduct.ProfitMarginRetail : _initialProduct.ProfitPercentage;
            PriceRetailUSD = _initialProduct.PriceRetailUSD > 0 ? _initialProduct.PriceRetailUSD : _initialProduct.PriceUSD;
            HasWholesale = _initialProduct.HasWholesale;
            ProfitMarginWholesale = _initialProduct.ProfitMarginWholesale;
            PriceWholesaleUSD = _initialProduct.PriceWholesaleUSD;
            MinWholesaleQuantity = _initialProduct.MinWholesaleQuantity > 0m ? _initialProduct.MinWholesaleQuantity : 6.000m;
            IsFractional = _initialProduct.IsFractional;
            UnitOfMeasureType = _initialProduct.UnitOfMeasure;

            StockQuantity = _initialProduct.StockQuantity;
            LowStockThreshold = _initialProduct.LowStockThreshold;
            IsCashAdvance = _initialProduct.IsCashAdvance;
            ConversionFactor = _initialProduct.ConversionFactor > 0 ? _initialProduct.ConversionFactor : 1.0000m;
        }
        else
        {
            HasWholesale = false;
            MinWholesaleQuantity = 6.000m;
            PriceBsS = 0;
            ConversionFactor = 1.0000m;
        }

        // Initialize snapshot with initial pricing BEFORE CalculatePricing
        CaptureManualPricingSnapshot();

        CalculatePricing("Cost");

        _debounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _debounceTimer.Tick += OnDebounceTimerTick;

        LoadMetadataAsync().SafeFireAndForget("ProductDialogViewModel.LoadMetadata");
    }

    partial void OnIsCashAdvanceChanged(bool value)
    {
        if (value)
        {
            if (HasActiveVariants)
            {
                IsCashAdvance = false;
                ErrorMessage = "No se puede convertir a servicio de adelanto de efectivo un producto que posee variantes asociadas.";
                IsError = true;
                return;
            }

            IsGroupHeader = false;
            IsStockShared = false;
            HasIndependentPricing = false;
            SelectedParentProduct = ParentProducts.FirstOrDefault(p => p.Id == 0);
            ParentProductId = null;
            IsFractional = false;
            UnitOfMeasureType = Core.Entities.UnitOfMeasureType.Und;
            StockQuantity = 0m;
            LowStockThreshold = 0m;
        }

        OnPropertyChanged(nameof(ShowStockInputs));
        OnPropertyChanged(nameof(CanEditFractional));
        OnPropertyChanged(nameof(CanEditGroupHeader));
        OnPropertyChanged(nameof(CanSelectParentProduct));
        OnPropertyChanged(nameof(CanEditStockShared));
        OnPropertyChanged(nameof(CanEditIndependentPricing));
    }

    partial void OnSkuChanged(string value)
    {
        IsSkuValid = true;
        SkuVerificationMessage = string.Empty;

        if (IsGroupHeader)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value) || !System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^[A-Za-z0-9\-_]{1,50}$"))
        {
            IsSkuValid = false;
            SkuVerificationMessage = "El SKU/Código de barras debe contener entre 1 y 50 caracteres alfanuméricos (letras, dígitos o guiones).";
            return;
        }

        // Skip validation in edit mode if SKU hasn't changed
        if (IsEditMode && _initialProduct?.SKU == value) return;

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    [RelayCommand]
    public async Task LoadMetadataAsync()
    {
        IsLoadingMetadata = true;
        IsLoading = false;
        IsError = false;
        ErrorMessage = string.Empty;

        UnitOfMeasures.Clear();
        UnitOfMeasures.Add("Unit");
        UnitOfMeasures.Add("kg");
        UnitOfMeasures.Add("lt");
        UnitOfMeasures.Add("Meter");

        try
        {
            var parentsTask = _productService.GetParentsAsync();
            Task<List<ProductDto>>? variantsTask = (_initialProduct != null && _initialProduct.Id > 0 && _initialProduct.IsGroupHeader)
                ? _productService.GetVariantsAsync(_initialProduct.Id)
                : null;

            var parents = await parentsTask;
            ApplyParentProductsUpdate(parents);

            if (variantsTask != null)
            {
                var variants = await variantsTask;
                ActiveVariantsCount = variants.Count(v => !v.IsDeleted);
                HasActiveVariants = ActiveVariantsCount > 0;
            }
            else
            {
                ActiveVariantsCount = 0;
                HasActiveVariants = false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProductDialogViewModel] Error cargando metadatos: {ex.Message}");
            ApplyParentProductsUpdate(new List<ProductDto>());
        }
        finally
        {
            IsLoadingMetadata = false;
        }
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        VerifySkuAsync().SafeFireAndForget("ProductDialogViewModel.VerifySku");
    }

    private async Task VerifySkuAsync()
    {
        _debounceTimer.Stop();

        if (IsGroupHeader)
        {
            IsSkuValid = true;
            SkuVerificationMessage = string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(Sku) || !System.Text.RegularExpressions.Regex.IsMatch(Sku.Trim(), @"^[A-Za-z0-9\-_]{1,50}$"))
        {
            IsSkuValid = false;
            SkuVerificationMessage = "El SKU/Código de barras debe contener entre 1 y 50 caracteres alfanuméricos (letras, dígitos o guiones).";
            return;
        }

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _skuCancellationTokenSource, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var token = newCts.Token;

        try
        {
            IsSkuVerifying = true;
            SkuVerificationMessage = "Verifying SKU availability...";

            var existingProduct = await _productService.GetQuickInfoAsync(Sku);

            if (!token.IsCancellationRequested)
            {
                if (existingProduct != null && existingProduct.Id != _initialProduct?.Id)
                {
                    IsSkuValid = false;
                    SkuVerificationMessage = "SKU already exists in the catalog.";
                }
                else
                {
                    IsSkuValid = true;
                    SkuVerificationMessage = string.Empty;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore token cancel
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash(ex, "ProductDialogViewModel.VerifySkuAsync");
            if (!token.IsCancellationRequested)
            {
                IsSkuValid = true;
                SkuVerificationMessage = $"Warning: Could not verify SKU: {ex.Message}";
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSkuVerifying = false;
            }
        }
    }

    public void Dispose()
    {
        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTimerTick;
        try
        {
            _skuCancellationTokenSource?.Cancel();
            _skuCancellationTokenSource?.Dispose();
        }
        catch (ObjectDisposedException) { }
        _skuCancellationTokenSource = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        CalculatePricing("Cost");
        ValidateAllProperties();

        if (IsGroupHeader)
        {
            IsSkuValid = true;
            SkuVerificationMessage = string.Empty;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Sku) || !System.Text.RegularExpressions.Regex.IsMatch(Sku.Trim(), @"^[A-Za-z0-9\-_]{1,50}$"))
            {
                IsSkuValid = false;
                SkuVerificationMessage = "El SKU/Código de barras debe contener entre 1 y 50 caracteres alfanuméricos (letras, dígitos o guiones).";
                return;
            }

            await VerifySkuAsync();
            if (!IsSkuValid)
            {
                return;
            }
        }

        if (HasErrors || !IsSkuValid || IsSkuVerifying)
        {
            return;
        }

        if (IsCashAdvance && (IsGroupHeader || (SelectedParentProduct != null && SelectedParentProduct.Id > 0)))
        {
            ErrorMessage = "Un servicio de adelanto de efectivo no puede ser un grupo ni pertenecer a un producto padre.";
            IsError = true;
            return;
        }

        ResultProduct.Name = Name.Trim();
        ResultProduct.Description = Description.Trim();

        if (IsGroupHeader && HasIndependentPricing)
        {
            ResultProduct.CostPriceUSD = 0m;
            ResultProduct.Cost = 0m;
            ResultProduct.ProfitMarginRetail = 0m;
            ResultProduct.ProfitPercentage = 0m;
            ResultProduct.PriceRetailUSD = 0m;
            ResultProduct.PriceUSD = 0m;
            ResultProduct.HasWholesale = false;
            ResultProduct.ProfitMarginWholesale = 0m;
            ResultProduct.PriceWholesaleUSD = 0m;
            ResultProduct.PriceBsS = 0m;
            ResultProduct.MinWholesaleQuantity = 6.000m;
        }
        else
        {
            ResultProduct.CostPriceUSD = CostPriceUSD;
            ResultProduct.Cost = CostPriceUSD;
            ResultProduct.ProfitMarginRetail = ProfitMarginRetail;
            ResultProduct.ProfitPercentage = ProfitMarginRetail;
            ResultProduct.PriceRetailUSD = PriceRetailUSD;
            ResultProduct.PriceUSD = PriceRetailUSD;
            ResultProduct.HasWholesale = HasWholesale;
            ResultProduct.ProfitMarginWholesale = HasWholesale ? ProfitMarginWholesale : ProfitMarginRetail;
            ResultProduct.PriceWholesaleUSD = HasWholesale ? PriceWholesaleUSD : PriceRetailUSD;
            ResultProduct.MinWholesaleQuantity = MinWholesaleQuantity > 0m ? MinWholesaleQuantity : 6.000m;
            decimal rate = _exchangeRateService.CurrentRate;
            ResultProduct.PriceBsS = (rate > 0 && PriceRetailUSD > 0)
                ? Math.Round(PriceRetailUSD * rate, 2, MidpointRounding.AwayFromZero)
                : (PriceRetailBsS > 0 ? PriceRetailBsS : (_initialProduct?.PriceBsS ?? 0m));
        }

        ResultProduct.IsFractional = IsCashAdvance ? false : IsFractional;
        ResultProduct.UnitOfMeasure = IsCashAdvance ? Core.Entities.UnitOfMeasureType.Und : UnitOfMeasureType;

        ResultProduct.IsCashAdvance = IsCashAdvance;
        ResultProduct.IsGroupHeader = IsCashAdvance ? false : IsGroupHeader;
        ResultProduct.IsStockShared = IsGroupHeader ? IsStockShared : false;
        ResultProduct.HasIndependentPricing = IsGroupHeader ? HasIndependentPricing : false;
        ResultProduct.ParentProductId = (IsGroupHeader || IsCashAdvance) ? null : (SelectedParentProduct != null && SelectedParentProduct.Id > 0 ? SelectedParentProduct.Id : null);
        ResultProduct.GroupKey = IsCashAdvance ? null : (string.IsNullOrWhiteSpace(GroupKey) ? (IsGroupHeader ? Name.Trim() : null) : GroupKey.Trim());

        if (IsGroupHeader && string.IsNullOrWhiteSpace(Sku))
        {
            ResultProduct.SKU = (!string.IsNullOrWhiteSpace(_initialProduct?.SKU) && _initialProduct.SKU.StartsWith("GRP-")) 
                ? _initialProduct.SKU 
                : $"GRP-{DateTime.UtcNow.Ticks}";
        }
        else
        {
            ResultProduct.SKU = Sku.Trim();
        }

        // Stock quantity can only be set initially. Existing products must use stock adjust.
        bool isSharedChild = SelectedParentProduct != null && SelectedParentProduct.Id > 0 && SelectedParentProduct.IsStockShared;

        ResultProduct.ConversionFactor = (isSharedChild && ConversionFactor > 0) ? ConversionFactor : 1.0000m;
        ResultProduct.LowStockThreshold = (IsCashAdvance || (IsGroupHeader && !IsStockShared) || isSharedChild) ? 0m : LowStockThreshold;

        if (!IsEditMode)
        {
            ResultProduct.StockQuantity = (IsCashAdvance || (IsGroupHeader && !IsStockShared) || isSharedChild) ? 0m : StockQuantity;
        }

        if (_initialProduct != null)
        {
            ResultProduct.Id = _initialProduct.Id;
            ResultProduct.IsActive = _initialProduct.IsActive;
            ResultProduct.ReservedQuantity = (IsCashAdvance || (IsGroupHeader && !IsStockShared) || isSharedChild) ? 0m : _initialProduct.ReservedQuantity;
            ResultProduct.RowVersion = _initialProduct.RowVersion;

            if (IsEditMode)
            {
                ResultProduct.StockQuantity = (IsGroupHeader && !IsStockShared) ? 0m : _initialProduct.StockQuantity;
            }
        }

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
