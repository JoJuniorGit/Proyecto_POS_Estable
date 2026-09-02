using CommunityToolkit.Mvvm.ComponentModel;
using Core.DTOs;
using System;

namespace Desktop.Client.ViewModels;

public partial class ProductItemViewModel : ObservableObject
{
    private readonly ProductDto _dto;
    private readonly Services.IExchangeRateService _exchangeRateService;
    private readonly Action<ProductItemViewModel> _onChanged;
    private bool _isCalculating;

    public int Id => _dto.Id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _sKU = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayCost))]
    private decimal _cost;

    [ObservableProperty]
    private decimal _stockQuantity;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdjustStock))]
    [NotifyPropertyChangedFor(nameof(AdjustStockToolTip))]
    private bool _isDeleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayWholesalePrice))]
    private decimal _priceWholesaleUSD;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayWholesalePrice))]
    private decimal _priceWholesaleBsS;

    [ObservableProperty]
    private decimal _minWholesaleQuantity;

    [ObservableProperty]
    private bool _hasWholesale;

    [ObservableProperty]
    private string _unitOfMeasureStr = "Und";

    [ObservableProperty]
    private string _selectedCurrency = "Bs.S";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayCost))]
    [NotifyPropertyChangedFor(nameof(DisplayRetailPrice))]
    [NotifyPropertyChangedFor(nameof(DisplayWholesalePrice))]
    [NotifyPropertyChangedFor(nameof(FormattedStockQuantity))]
    [NotifyPropertyChangedFor(nameof(IsStockCritical))]
    [NotifyPropertyChangedFor(nameof(CanAdjustStock))]
    [NotifyPropertyChangedFor(nameof(AdjustStockToolTip))]
    private bool _isGroupHeader;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdjustStock))]
    [NotifyPropertyChangedFor(nameof(AdjustStockToolTip))]
    [NotifyPropertyChangedFor(nameof(FormattedStockQuantity))]
    private bool _isStockShared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdjustStock))]
    [NotifyPropertyChangedFor(nameof(AdjustStockToolTip))]
    private bool _parentIsStockShared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayCost))]
    [NotifyPropertyChangedFor(nameof(DisplayRetailPrice))]
    [NotifyPropertyChangedFor(nameof(DisplayWholesalePrice))]
    private bool _hasIndependentPricing;

    public int? ParentProductId => _dto.ParentProductId;

    public bool IsInactive => !IsActive && !IsDeleted;

    public string StatusLabel => IsDeleted ? "ELIMINADO (HISTORIAL)" : (!IsActive ? "INACTIVO" : "ACTIVO");

    public bool HasRealWholesale => (HasWholesale || PriceWholesaleUSD > 0) && PriceWholesaleUSD > 0 && PriceWholesaleUSD < PriceUSD;

    public decimal EffectivePriceWholesaleUSD => HasRealWholesale ? PriceWholesaleUSD : PriceUSD;

    public decimal EffectivePriceWholesaleBsS => HasRealWholesale 
        ? (PriceWholesaleUSD > 0 ? Math.Round(PriceWholesaleUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero) : PriceBsS) 
        : PriceBsS;

    public decimal EffectiveMinWholesaleQuantity => HasRealWholesale ? (MinWholesaleQuantity > 0 ? MinWholesaleQuantity : 1m) : 1m;

    public bool IsCashAdvance => _dto.IsCashAdvance;
    public decimal ConsolidatedStock => _dto.ConsolidatedStock;

    public bool IsStockCritical => !IsCashAdvance && !IsGroupHeader && StockQuantity <= 0;

    public string FormattedStockQuantity
    {
        get
        {
            if (IsCashAdvance) return "Servicio";
            if (IsGroupHeader) return $"{ConsolidatedStock:#,##0.###} (Consolidado)";
            return StockQuantity.ToString("#,##0.###", System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    partial void OnStockQuantityChanged(decimal value)
    {
        OnPropertyChanged(nameof(FormattedStockQuantity));
        OnPropertyChanged(nameof(IsStockCritical));
    }

    public string FormattedMinWholesaleQuantityStr => ((int)Math.Round(EffectiveMinWholesaleQuantity, MidpointRounding.AwayFromZero)).ToString();

    public string WholesalePriceColor => HasRealWholesale ? "#6366F1" : "#D97706";

    public string DisplayCost => (IsGroupHeader && HasIndependentPricing) ? "—" : $"${Cost:N2}";

    public string DisplayRetailPrice
    {
        get
        {
            if (IsGroupHeader && HasIndependentPricing) return "—";
            if (SelectedCurrency == "USD") return $"${PriceUSD:N2}";
            decimal bss = PriceBsS > 0 
                ? PriceBsS 
                : (_exchangeRateService.CurrentRate > 0 ? Math.Round(PriceUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero) : 0m);
            return $"Bs.S {bss:N2}";
        }
    }

    public string DisplayWholesalePrice
    {
        get
        {
            if (IsGroupHeader && HasIndependentPricing) return "—";
            if (SelectedCurrency == "USD") return $"${EffectivePriceWholesaleUSD:N2}";
            decimal bss = EffectivePriceWholesaleBsS > 0 
                ? EffectivePriceWholesaleBsS 
                : (_exchangeRateService.CurrentRate > 0 ? Math.Round(EffectivePriceWholesaleUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero) : 0m);
            return $"Bs.S {bss:N2}";
        }
    }

    public bool CanAdjustStock =>
        !IsCashAdvance &&
        !IsDeleted &&
        (IsGroupHeader ? IsStockShared : (ParentProductId.HasValue ? !ParentIsStockShared : true));

    public string AdjustStockToolTip
    {
        get
        {
            if (IsDeleted) return Core.Constants.InventoryMessages.TooltipDeleted;
            if (IsCashAdvance) return Core.Constants.InventoryMessages.TooltipCashAdvance;
            if (IsGroupHeader && !IsStockShared)
                return Core.Constants.InventoryMessages.TooltipGroupIndividualBlocked;
            if (ParentProductId.HasValue && ParentIsStockShared)
                return Core.Constants.InventoryMessages.TooltipVariantSharedBlocked;
            return Core.Constants.InventoryMessages.TooltipAdjustStockAllowed;
        }
    }

    public ProductItemViewModel(ProductDto dto, Services.IExchangeRateService exchangeRateService, Action<ProductItemViewModel>? onChanged = null)
    {
        _dto = dto;
        _exchangeRateService = exchangeRateService;
        _onChanged = onChanged ?? (_ => {});
        
        // Initial values
        _name = dto.Name;
        _sKU = dto.SKU;
        _cost = dto.Cost;
        _stockQuantity = dto.StockQuantity;
        _profitPercentage = dto.ProfitPercentage;
        _priceUSD = dto.PriceUSD;
        _priceBsS = dto.PriceBsS > 0 ? dto.PriceBsS : (_exchangeRateService.CurrentRate > 0 ? Math.Round(dto.PriceUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero) : 0m);
        
        _priceWholesaleUSD = dto.PriceWholesaleUSD;
        _priceWholesaleBsS = dto.PriceWholesaleUSD > 0 
            ? (_exchangeRateService.CurrentRate > 0 ? Math.Round(dto.PriceWholesaleUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero) : 0m) 
            : 0m;
        _minWholesaleQuantity = dto.MinWholesaleQuantity;
        _hasWholesale = dto.HasWholesale || dto.PriceWholesaleUSD > 0;
        _unitOfMeasureStr = dto.UnitOfMeasureStr;

        _isGroupHeader = dto.IsGroupHeader;
        _isStockShared = dto.IsStockShared;
        _parentIsStockShared = dto.ParentIsStockShared;
        _hasIndependentPricing = dto.HasIndependentPricing;

        _isActive = dto.IsActive;
        _isDeleted = dto.IsDeleted;
    }

    public void NotifyCurrencyChanged(string newCurrency)
    {
        SelectedCurrency = newCurrency;
        OnPropertyChanged(nameof(DisplayCost));
        OnPropertyChanged(nameof(DisplayRetailPrice));
        OnPropertyChanged(nameof(DisplayWholesalePrice));
    }

    public void UpdateExchangeRate()
    {
        if (_isCalculating) return;
        PriceBsS = Math.Round(PriceUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero);
        PriceWholesaleBsS = Math.Round(PriceWholesaleUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero);
        OnPropertyChanged(nameof(DisplayRetailPrice));
        OnPropertyChanged(nameof(DisplayWholesalePrice));
    }

    public void UpdateFromDto(ProductDto dto)
    {
        _isCalculating = true;
        try
        {
            _dto.Name = dto.Name;
            _dto.SKU = dto.SKU;
            _dto.Description = dto.Description;
            _dto.Cost = dto.Cost;
            _dto.StockQuantity = dto.StockQuantity;
            _dto.ProfitPercentage = dto.ProfitPercentage;
            _dto.PriceUSD = dto.PriceUSD;
            _dto.PriceBsS = dto.PriceBsS;
            _dto.PriceWholesaleUSD = dto.PriceWholesaleUSD;
            _dto.MinWholesaleQuantity = dto.MinWholesaleQuantity;
            _dto.HasWholesale = dto.HasWholesale;
            _dto.UnitOfMeasure = dto.UnitOfMeasure;
            _dto.LowStockThreshold = dto.LowStockThreshold;
            _dto.IsCashAdvance = dto.IsCashAdvance;
            _dto.IsGroupHeader = dto.IsGroupHeader;
            _dto.IsStockShared = dto.IsStockShared;
            _dto.ParentIsStockShared = dto.ParentIsStockShared;
            _dto.HasIndependentPricing = dto.HasIndependentPricing;
            _dto.ConsolidatedStock = dto.ConsolidatedStock;
            _dto.ParentProductId = dto.ParentProductId;
            _dto.ReservedQuantity = dto.ReservedQuantity;

            Name = dto.Name;
            SKU = dto.SKU;
            Cost = dto.Cost;
            StockQuantity = dto.StockQuantity;
            ProfitPercentage = dto.ProfitPercentage;
            PriceUSD = dto.PriceUSD;
            PriceBsS = dto.PriceBsS > 0 
                ? dto.PriceBsS 
                : (_exchangeRateService.CurrentRate > 0 ? Math.Round(dto.PriceUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero) : 0m);

            PriceWholesaleUSD = dto.PriceWholesaleUSD;
            PriceWholesaleBsS = dto.PriceWholesaleUSD > 0 
                ? (_exchangeRateService.CurrentRate > 0 ? Math.Round(dto.PriceWholesaleUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero) : 0m) 
                : 0m;
            MinWholesaleQuantity = dto.MinWholesaleQuantity;
            HasWholesale = dto.HasWholesale || dto.PriceWholesaleUSD > 0;
            UnitOfMeasureStr = dto.UnitOfMeasureStr;

            IsGroupHeader = dto.IsGroupHeader;
            IsStockShared = dto.IsStockShared;
            ParentIsStockShared = dto.ParentIsStockShared;
            HasIndependentPricing = dto.HasIndependentPricing;

            IsActive = dto.IsActive;
            IsDeleted = dto.IsDeleted;

            OnPropertyChanged(nameof(IsInactive));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(IsCashAdvance));
            OnPropertyChanged(nameof(IsGroupHeader));
            OnPropertyChanged(nameof(IsStockShared));
            OnPropertyChanged(nameof(ParentIsStockShared));
            OnPropertyChanged(nameof(HasIndependentPricing));
            OnPropertyChanged(nameof(ConsolidatedStock));
            OnPropertyChanged(nameof(HasRealWholesale));
            OnPropertyChanged(nameof(EffectiveMinWholesaleQuantity));
            OnPropertyChanged(nameof(FormattedMinWholesaleQuantityStr));
            OnPropertyChanged(nameof(IsStockCritical));
            OnPropertyChanged(nameof(FormattedStockQuantity));
            OnPropertyChanged(nameof(WholesalePriceColor));
            OnPropertyChanged(nameof(DisplayCost));
            OnPropertyChanged(nameof(DisplayRetailPrice));
            OnPropertyChanged(nameof(DisplayWholesalePrice));
            OnPropertyChanged(nameof(CanAdjustStock));
            OnPropertyChanged(nameof(AdjustStockToolTip));

        }
        finally
        {
            _isCalculating = false;
        }
    }

    [ObservableProperty]
    private decimal _profitPercentage;

    [ObservableProperty]
    private decimal _priceUSD;

    [ObservableProperty]
    private decimal _priceBsS;

    partial void OnProfitPercentageChanged(decimal value)
    {
        if (_isCalculating) return;
        _isCalculating = true;

        try
        {
            PriceUSD = Math.Round(Cost * (1 + (value / 100m)), 2, MidpointRounding.AwayFromZero);
            PriceBsS = Math.Round(PriceUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero);
            
            _dto.ProfitPercentage = value;
            _dto.PriceUSD = PriceUSD;
            _dto.PriceBsS = PriceBsS;

            _onChanged?.Invoke(this);
            OnPropertyChanged(nameof(DisplayRetailPrice));
            OnPropertyChanged(nameof(DisplayWholesalePrice));
        }
        finally
        {
            _isCalculating = false;
        }
    }

    public ProductDto GetDto() => _dto;
}
