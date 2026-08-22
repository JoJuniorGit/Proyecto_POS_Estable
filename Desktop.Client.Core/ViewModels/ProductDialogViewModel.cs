using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    [Required(ErrorMessage = "SKU is required")]
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
    private Core.Entities.UnitOfMeasureType _unitOfMeasureType = Core.Entities.UnitOfMeasureType.Und;

    [ObservableProperty]
    private bool _isSellingAtLoss;

    [ObservableProperty]
    private decimal _priceBsS;

    private bool _isUpdatingPrices;

    [ObservableProperty]
    [Range(0, int.MaxValue, ErrorMessage = "Stock Quantity must be non-negative")]
    private int _stockQuantity;

    [ObservableProperty]
    [Range(0, int.MaxValue, ErrorMessage = "Low Stock Threshold must be non-negative")]
    private int _lowStockThreshold;

    [ObservableProperty]
    private string _unitOfMeasure = "Unit";

    [ObservableProperty]
    private bool _isService;

    [ObservableProperty]
    private bool _isCashAdvance;

    [ObservableProperty]
    private bool _isEditMode;

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

    public ObservableCollection<Core.Entities.UnitOfMeasureType> UnitOfMeasureTypes { get; } = new(Enum.GetValues<Core.Entities.UnitOfMeasureType>());
    public ObservableCollection<string> UnitOfMeasures { get; } = new ObservableCollection<string>();

    public UserSession? UserSession { get; }

    public ProductDialogViewModel(IProductService productService, IExchangeRateService exchangeRateService, Product? product = null, UserSession? userSession = null)
    {
        _productService = productService;
        _exchangeRateService = exchangeRateService;
        _initialProduct = product;
        UserSession = userSession;

        IsEditMode = product != null;
        DialogTitle = IsEditMode ? "Edit Product" : "Add New Product";
        ResultProduct = new Product { IsActive = true };

        if (_initialProduct != null)
        {
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

            CalculatePricing("Cost");
        }
        else
        {
            HasWholesale = false;
            MinWholesaleQuantity = 6.000m;
            PriceBsS = 0;
        }

        _debounceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _debounceTimer.Tick += async (s, e) => await VerifySkuAsync();

        LoadMetadata();
    }

    partial void OnSkuChanged(string value)
    {
        IsSkuValid = true;
        SkuVerificationMessage = string.Empty;

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

    [RelayCommand]
    private void LoadMetadata()
    {
        IsLoading = false;
        IsError = false;
        ErrorMessage = string.Empty;

        UnitOfMeasures.Clear();
        UnitOfMeasures.Add("Unit");
        UnitOfMeasures.Add("kg");
        UnitOfMeasures.Add("lt");
        UnitOfMeasures.Add("Meter");
    }

    [RelayCommand]
    public void RecalculatePricing(string trigger)
    {
        CalculatePricing(string.IsNullOrEmpty(trigger) ? "Cost" : trigger);
    }

    partial void OnHasWholesaleChanged(bool value)
    {
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
    private void Save()
    {
        CalculatePricing("Cost");
        ValidateAllProperties();
        if (HasErrors || !IsSkuValid || IsSkuVerifying) return;

        ResultProduct.Name = Name.Trim();
        ResultProduct.SKU = Sku.Trim();
        ResultProduct.Description = Description.Trim();
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
        ResultProduct.IsFractional = IsFractional;
        decimal rate = _exchangeRateService.CurrentRate;
        ResultProduct.PriceBsS = (rate > 0 && PriceRetailUSD > 0)
            ? Math.Round(PriceRetailUSD * rate, 2, MidpointRounding.AwayFromZero)
            : (PriceRetailBsS > 0 ? PriceRetailBsS : (_initialProduct?.PriceBsS ?? 0m));

        ResultProduct.LowStockThreshold = LowStockThreshold;
        ResultProduct.IsCashAdvance = IsCashAdvance;

        // Stock quantity can only be set initially. Existing products must use stock adjust.
        if (!IsEditMode)
        {
            ResultProduct.StockQuantity = StockQuantity;
        }

        if (_initialProduct != null)
        {
            ResultProduct.Id = _initialProduct.Id;
            ResultProduct.IsActive = _initialProduct.IsActive;
            ResultProduct.ReservedQuantity = _initialProduct.ReservedQuantity;
            ResultProduct.RowVersion = _initialProduct.RowVersion;

            if (IsEditMode)
            {
                ResultProduct.StockQuantity = _initialProduct.StockQuantity;
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
