using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

public partial class ProductDialogViewModel : ObservableValidator
{
    private readonly IProductService _productService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly System.Windows.Threading.DispatcherTimer _debounceTimer;
    private CancellationTokenSource? _skuCancellationTokenSource;
    private readonly Product? _initialProduct;

    public Action<bool>? RequestClose;
    public Product ResultProduct { get; private set; }

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
    [Range(0, double.MaxValue, ErrorMessage = "Price must be non-negative")]
    private decimal _price; // Retail Price USD

    [ObservableProperty]
    [Range(0, double.MaxValue, ErrorMessage = "Cost must be non-negative")]
    private decimal _cost; // Gross cost USD

    [ObservableProperty]
    [Range(0, double.MaxValue, ErrorMessage = "Profit % must be non-negative")]
    private decimal _profitPercentage;

    [ObservableProperty]
    [Range(0, double.MaxValue, ErrorMessage = "Costo base debe ser positivo")]
    private decimal _costPriceUSD;

    [ObservableProperty]
    [Range(0, double.MaxValue, ErrorMessage = "Margen detal debe ser positivo")]
    private decimal _profitMarginRetail;

    [ObservableProperty]
    [Range(0, double.MaxValue, ErrorMessage = "Precio detal debe ser positivo")]
    private decimal _priceRetailUSD;

    [ObservableProperty]
    private decimal _priceRetailBsS;

    [ObservableProperty]
    [Range(0, double.MaxValue, ErrorMessage = "Margen mayor debe ser positivo")]
    private decimal _profitMarginWholesale;

    [ObservableProperty]
    [Range(0, double.MaxValue, ErrorMessage = "Precio mayor debe ser positivo")]
    private decimal _priceWholesaleUSD;

    [ObservableProperty]
    private decimal _priceWholesaleBsS;

    [ObservableProperty]
    private decimal _minWholesaleQuantity = 6.000m;

    [ObservableProperty]
    private bool _hasWholesale;

    [ObservableProperty]
    private bool _isFractional;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStockDisplay))]
    private Core.Entities.UnitOfMeasureType _unitOfMeasureType = Core.Entities.UnitOfMeasureType.Und;

    [ObservableProperty]
    private bool _isSellingAtLoss;

    [ObservableProperty]
    private decimal _priceBsS;

    private bool _isUpdatingPrices;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditStockShared))]
    [NotifyPropertyChangedFor(nameof(CanEditIndependentPricing))]
    [NotifyPropertyChangedFor(nameof(ShowStockInputs))]
    [NotifyPropertyChangedFor(nameof(ShowConversionFactorInput))]
    [NotifyPropertyChangedFor(nameof(ShowManageVariantsButton))]
    [NotifyPropertyChangedFor(nameof(ShowPricingInputs))]
    [NotifyPropertyChangedFor(nameof(ShowIndependentPricingNotice))]
    [NotifyPropertyChangedFor(nameof(CanEditPricing))]
    [NotifyPropertyChangedFor(nameof(CanEditWholesale))]
    [NotifyPropertyChangedFor(nameof(CanEditFractional))]
    private bool _isGroupHeader;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditStockShared))]
    [NotifyPropertyChangedFor(nameof(CanEditIndependentPricing))]
    [NotifyPropertyChangedFor(nameof(ShowStockInputs))]
    private bool _isStockShared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditStockShared))]
    [NotifyPropertyChangedFor(nameof(CanEditIndependentPricing))]
    [NotifyPropertyChangedFor(nameof(ShowPricingInputs))]
    [NotifyPropertyChangedFor(nameof(ShowIndependentPricingNotice))]
    [NotifyPropertyChangedFor(nameof(CanEditPricing))]
    [NotifyPropertyChangedFor(nameof(CanEditWholesale))]
    private bool _hasIndependentPricing;

    [ObservableProperty]
    private int? _parentProductId;

    [ObservableProperty]
    private string? _groupKey;

    [ObservableProperty]
    private ObservableCollection<ProductDto> _parentProducts = new();

    [ObservableProperty]
    private ProductDto? _selectedParentProduct;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditGroupHeader))]
    [NotifyPropertyChangedFor(nameof(CanSelectParentProduct))]
    [NotifyPropertyChangedFor(nameof(GroupHeaderToolTip))]
    private bool _hasActiveVariants;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupHeaderToolTip))]
    private int _activeVariantsCount;

    [ObservableProperty]
    [Range(0.0001, 1000000.0, ErrorMessage = "El factor de conversión debe ser mayor a 0")]
    private decimal _conversionFactor = 1.0000m;

    public bool IsVariant => SelectedParentProduct != null && SelectedParentProduct.Id > 0;
    public bool IsInheritingPricing => SelectedParentProduct != null && SelectedParentProduct.Id > 0 && !SelectedParentProduct.HasIndependentPricing;

    public bool CanEditStockShared => IsCreateMode && IsGroupHeader && !IsCashAdvance;
    public bool CanEditIndependentPricing => IsCreateMode && IsGroupHeader && !IsCashAdvance;
    public bool ShowManageVariantsButton => IsEditMode && IsGroupHeader;

    public bool ShowPricingInputs => !IsCashAdvance && !(IsGroupHeader && HasIndependentPricing);
    public bool ShowIndependentPricingNotice => !IsCashAdvance && IsGroupHeader && HasIndependentPricing;

    public bool CanEditPricing => !IsInheritingPricing && !(IsGroupHeader && HasIndependentPricing);
    public bool CanEditWholesale => HasWholesale && !IsInheritingPricing && !(IsGroupHeader && HasIndependentPricing);
    public bool CanEditFractional => !IsCashAdvance && !IsGroupHeader && !IsInheritingPricing;
    public bool CanEditGroupHeader => !IsCashAdvance && (SelectedParentProduct == null || SelectedParentProduct.Id == 0) && !HasActiveVariants;
    public bool CanSelectParentProduct => !HasActiveVariants && !IsCashAdvance;
    public bool ShowStockInputs => !IsCashAdvance && (!IsGroupHeader || (IsGroupHeader && IsStockShared)) && !(SelectedParentProduct != null && SelectedParentProduct.Id > 0 && SelectedParentProduct.IsStockShared);
    public bool ShowConversionFactorInput => !IsCashAdvance && !IsGroupHeader && SelectedParentProduct != null && SelectedParentProduct.Id > 0 && SelectedParentProduct.IsStockShared;

    public string GroupHeaderToolTip => HasActiveVariants 
        ? $"Este producto es un grupo con {ActiveVariantsCount} variante(s) asociada(s). No puede convertirse en producto independiente; elimine o desvincule primero las variantes."
        : "Activa esta opción si viene en diferentes sabores, tallas o capacidades.";

    // Snapshot fields for restoring manual pricing when switching back to "Ninguno"
    private decimal _origCostPriceUSD;
    private decimal _origProfitMarginRetail;
    private decimal _origPriceRetailUSD;
    private bool _origHasWholesale;
    private decimal _origProfitMarginWholesale;
    private decimal _origPriceWholesaleUSD;
    private decimal _origMinWholesaleQuantity;
    private bool _origIsFractional;
    private Core.Entities.UnitOfMeasureType _origUnitOfMeasureType;

    public ObservableCollection<Core.Entities.UnitOfMeasureType> UnitOfMeasureTypes { get; } = new(Enum.GetValues<Core.Entities.UnitOfMeasureType>());
    public ObservableCollection<string> UnitOfMeasures { get; } = new ObservableCollection<string>();

    private readonly IDialogService? _dialogService;
    public UserSession? UserSession { get; }

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
        _debounceTimer.Tick += async (s, e) => await VerifySkuAsync();

        _ = LoadMetadataAsync();
    }

    private void CaptureManualPricingSnapshot()
    {
        _origCostPriceUSD = CostPriceUSD;
        _origProfitMarginRetail = ProfitMarginRetail;
        _origPriceRetailUSD = PriceRetailUSD;
        _origHasWholesale = HasWholesale;
        _origProfitMarginWholesale = ProfitMarginWholesale;
        _origPriceWholesaleUSD = PriceWholesaleUSD;
        _origMinWholesaleQuantity = MinWholesaleQuantity > 0m ? MinWholesaleQuantity : 6.000m;
        _origIsFractional = IsFractional;
        _origUnitOfMeasureType = UnitOfMeasureType;
    }

    private void RestoreManualPricingSnapshot()
    {
        _isUpdatingPrices = true;
        try
        {
            CostPriceUSD = _origCostPriceUSD;
            ProfitMarginRetail = _origProfitMarginRetail;
            PriceRetailUSD = _origPriceRetailUSD;
            HasWholesale = _origHasWholesale;
            ProfitMarginWholesale = _origProfitMarginWholesale;
            PriceWholesaleUSD = _origPriceWholesaleUSD;
            MinWholesaleQuantity = _origMinWholesaleQuantity;
            IsFractional = _origIsFractional;
            UnitOfMeasureType = _origUnitOfMeasureType;
        }
        finally
        {
            _isUpdatingPrices = false;
        }
        CalculatePricing("Cost");
    }

    partial void OnSelectedParentProductChanged(ProductDto? value)
    {
        OnPropertyChanged(nameof(IsVariant));
        OnPropertyChanged(nameof(IsInheritingPricing));
        OnPropertyChanged(nameof(CanEditPricing));
        OnPropertyChanged(nameof(CanEditWholesale));
        OnPropertyChanged(nameof(CanEditFractional));
        OnPropertyChanged(nameof(CanEditGroupHeader));
        OnPropertyChanged(nameof(CanSelectParentProduct));
        OnPropertyChanged(nameof(ShowStockInputs));
        OnPropertyChanged(nameof(ShowConversionFactorInput));

        if (value != null && value.Id > 0)
        {
            if (HasActiveVariants)
            {
                SelectedParentProduct = ParentProducts.FirstOrDefault(p => p.Id == 0);
                ErrorMessage = "Un producto con variantes asociadas no puede ser asignado como variante de otro padre.";
                IsError = true;
                return;
            }

            IsGroupHeader = false;
            IsStockShared = false;
            HasIndependentPricing = false;

            if (value.IsStockShared)
            {
                StockQuantity = 0m;
                LowStockThreshold = 0m;
            }

            if (!value.HasIndependentPricing)
            {
                if (ParentProductId == null)
                {
                    CaptureManualPricingSnapshot();
                }

                _isUpdatingPrices = true;
                try
                {
                    ParentProductId = value.Id;
                    CostPriceUSD = value.CostPriceUSD;
                    ProfitMarginRetail = value.ProfitMarginRetail;
                    PriceRetailUSD = value.PriceRetailUSD;
                    HasWholesale = value.HasWholesale;
                    ProfitMarginWholesale = value.ProfitMarginWholesale;
                    PriceWholesaleUSD = value.PriceWholesaleUSD;
                    MinWholesaleQuantity = value.MinWholesaleQuantity > 0m ? value.MinWholesaleQuantity : 6.000m;
                    IsFractional = value.IsFractional;
                    UnitOfMeasureType = value.UnitOfMeasure;
                }
                finally
                {
                    _isUpdatingPrices = false;
                }
                CalculatePricing("Cost");
            }
            else
            {
                ParentProductId = value.Id;
            }
        }
        else
        {
            ParentProductId = null;
            RestoreManualPricingSnapshot();
        }
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

    partial void OnIsGroupHeaderChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditFractional));
        OnPropertyChanged(nameof(CanEditGroupHeader));
        OnPropertyChanged(nameof(CanEditStockShared));
        OnPropertyChanged(nameof(CanEditIndependentPricing));
        OnPropertyChanged(nameof(ShowStockInputs));
        OnPropertyChanged(nameof(ShowManageVariantsButton));

        if (value)
        {
            IsCashAdvance = false;
            SelectedParentProduct = ParentProducts.FirstOrDefault(p => p.Id == 0);
            ParentProductId = null;
            if (!IsStockShared)
            {
                StockQuantity = 0;
                LowStockThreshold = 0;
            }
            IsSkuValid = true;
            SkuVerificationMessage = string.Empty;
            RestoreManualPricingSnapshot();
        }
        else
        {
            IsStockShared = false;
            HasIndependentPricing = false;
            if (!string.IsNullOrWhiteSpace(Sku))
            {
                OnSkuChanged(Sku);
            }
            else
            {
                IsSkuValid = false;
                SkuVerificationMessage = "El código SKU es obligatorio.";
            }
        }
    }

    partial void OnIsStockSharedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStockInputs));
        if (IsGroupHeader && !value)
        {
            StockQuantity = 0;
            LowStockThreshold = 0;
        }
    }

    partial void OnSkuChanged(string value)
    {
        IsSkuValid = true;
        SkuVerificationMessage = string.Empty;

        if (IsGroupHeader)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value) || !System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^\d+$"))
        {
            IsSkuValid = false;
            SkuVerificationMessage = "El SKU debe ser estrictamente un número entero (solo dígitos 0-9).";
            return;
        }

        // Skip validation in edit mode if SKU hasn't changed
        if (IsEditMode && _initialProduct?.SKU == value) return;

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void ApplyParentProductsUpdate(List<ProductDto> parents)
    {
        void Update()
        {
            ParentProducts.Clear();
            ParentProducts.Add(new ProductDto { Id = 0, Name = "Ninguno (Producto Independiente)" });
            foreach (var p in parents)
            {
                if (p.Id != _initialProduct?.Id)
                {
                    ParentProducts.Add(p);
                }
            }            if (_initialProduct?.ParentProductId.HasValue == true && _initialProduct.ParentProductId.Value > 0)
            {
                SelectedParentProduct = ParentProducts.FirstOrDefault(p => p.Id == _initialProduct.ParentProductId.Value)
                                         ?? ParentProducts.FirstOrDefault(p => p.Id == 0);
            }
            else
            {
                SelectedParentProduct = ParentProducts.FirstOrDefault(p => p.Id == 0);
            }
        }

        if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(Update);
        }
        else
        {
            Update();
        }
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

    [RelayCommand]
    public void RecalculatePricing(string trigger)
    {
        CalculatePricing(string.IsNullOrEmpty(trigger) ? "Cost" : trigger);
    }

    partial void OnHasWholesaleChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditWholesale));
        if (!value)
        {
            ProfitMarginWholesale = ProfitMarginRetail;
            PriceWholesaleUSD = PriceRetailUSD;
            PriceWholesaleBsS = PriceRetailBsS;
        }
        CalculatePricing("Cost");
    }

    private void CalculatePricing(string trigger)
    {
        if (_isUpdatingPrices) return;
        _isUpdatingPrices = true;

        try
        {
            if (trigger == "Cost" || trigger == "MarginRetail")
            {
                decimal rawPrice = CostPriceUSD * (1m + (ProfitMarginRetail / 100m));
                PriceRetailUSD = Math.Ceiling(rawPrice * 100m) / 100m;
            }
            else if (trigger == "PriceRetail")
            {
                if (CostPriceUSD > 0)
                {
                    decimal calculatedProfit = ((PriceRetailUSD / CostPriceUSD) - 1m) * 100m;
                    ProfitMarginRetail = calculatedProfit < 0 ? 0 : Math.Round(calculatedProfit, 2, MidpointRounding.AwayFromZero);
                }
                else
                {
                    ProfitMarginRetail = 100m;
                }
            }

            if (!HasWholesale)
            {
                ProfitMarginWholesale = ProfitMarginRetail;
                PriceWholesaleUSD = PriceRetailUSD;
            }
            else
            {
                if (trigger == "Cost" || trigger == "MarginWholesale")
                {
                    decimal rawWholesalePrice = CostPriceUSD * (1m + (ProfitMarginWholesale / 100m));
                    PriceWholesaleUSD = Math.Ceiling(rawWholesalePrice * 100m) / 100m;
                }
                else if (trigger == "PriceWholesale")
                {
                    if (CostPriceUSD > 0)
                    {
                        decimal calculatedProfit = ((PriceWholesaleUSD / CostPriceUSD) - 1m) * 100m;
                        ProfitMarginWholesale = calculatedProfit < 0 ? 0 : Math.Round(calculatedProfit, 2, MidpointRounding.AwayFromZero);
                    }
                    else
                    {
                        ProfitMarginWholesale = 100m;
                    }
                }
            }

            Price = PriceRetailUSD;
            Cost = CostPriceUSD;
            ProfitPercentage = ProfitMarginRetail;

            PriceRetailBsS = Math.Round(PriceRetailUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero);
            PriceWholesaleBsS = Math.Round(PriceWholesaleUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero);
            PriceBsS = PriceRetailBsS;

            IsSellingAtLoss = PriceRetailUSD > 0 && PriceRetailUSD < CostPriceUSD;
        }
        finally
        {
            _isUpdatingPrices = false;
        }
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

        if (string.IsNullOrWhiteSpace(Sku) || !System.Text.RegularExpressions.Regex.IsMatch(Sku.Trim(), @"^\d+$"))
        {
            IsSkuValid = false;
            SkuVerificationMessage = "El SKU debe ser estrictamente un número entero (solo dígitos 0-9).";
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

            // In a real microservice, you would query just the index without loading entire product entity arrays
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
            if (!token.IsCancellationRequested)
            {
                IsSkuValid = true; // Err on side of allowing if service fails to respond for some reason, robust write validation will catch it
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
            if (string.IsNullOrWhiteSpace(Sku) || !System.Text.RegularExpressions.Regex.IsMatch(Sku.Trim(), @"^\d+$"))
            {
                IsSkuValid = false;
                SkuVerificationMessage = "El SKU debe ser estrictamente un número entero (solo dígitos 0-9).";
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

    [RelayCommand]
    private async Task ManageVariantsAsync()
    {
        if (_initialProduct == null || !IsGroupHeader || _dialogService == null) return;
        var parentDto = new ProductDto
        {
            Id = _initialProduct.Id,
            Name = Name,
            SKU = Sku,
            IsGroupHeader = true,
            IsStockShared = IsStockShared,
            HasIndependentPricing = HasIndependentPricing,
            CostPriceUSD = CostPriceUSD,
            ProfitMarginRetail = ProfitMarginRetail,
            PriceRetailUSD = PriceRetailUSD,
            HasWholesale = HasWholesale,
            ProfitMarginWholesale = ProfitMarginWholesale,
            PriceWholesaleUSD = PriceWholesaleUSD,
            MinWholesaleQuantity = MinWholesaleQuantity,
            IsFractional = IsFractional,
            UnitOfMeasure = UnitOfMeasureType,
            RowVersion = _initialProduct.RowVersion
        };
        await _dialogService.ShowVariantManagementDialogAsync(parentDto);
        await LoadMetadataAsync();
    }
}
