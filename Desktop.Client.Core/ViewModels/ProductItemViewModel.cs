using CommunityToolkit.Mvvm.ComponentModel;
using Core.DTOs;
using Desktop.Client.Services;
using System;

namespace Desktop.Client.ViewModels;

public partial class ProductItemViewModel : ObservableObject
{
    private readonly ProductDto _dto;
    private readonly Services.IExchangeRateService _exchangeRateService;
    private readonly Action<ProductItemViewModel> _onChanged;
    private bool _isCalculating;

    // 8.185-T4 (S1/S2): política global vigente para el Bs.S efectivo de la fila. La fija el
    // contenedor con UNA lectura (0 consultas por fila) y se refresca por mensaje en memoria.
    private RoundingRuleType? _globalCashRoundingRule;
    private bool _neverRoundDownCashBs = true;

    public int Id => _dto.Id;

    /// <summary>
    /// 8.181-T2 (S5): marca de selección de la edición masiva de precios. Es estado de UI puro:
    /// NO dispara persistencia ni recálculos (a diferencia de <see cref="OnProfitPercentageChanged"/>);
    /// el contenedor decide qué hacer con el cambio vía <see cref="SelectionChanged"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Callback de cambio de selección; lo asigna el contenedor dueño de la grilla.</summary>
    public Action<ProductItemViewModel>? SelectionChanged { get; set; }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this);

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

    // 8.143: el Bs.S mostrado se deriva siempre de USD x tasa vigente; el snapshot PriceBsS
    // queda solo como fallback cuando no hay USD o la tasa no es valida.
    public decimal EffectivePriceWholesaleBsS => BsSFromRateOrFallback(EffectivePriceWholesaleUSD, PriceBsS);

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

    public string DisplayCost => (IsGroupHeader && HasIndependentPricing) ? "—" : $"${CurrencyDisplay.Number(Cost)}";

    // 8.143 + 8.185 (S1): todo precio Bs.S mostrado se deriva de USD × tasa vigente por el helper
    // único del cliente (regla efectiva del artículo ?? política global + toggle). ExactMath o sin
    // regla ⇒ techo canónico 8.104, byte-idéntico al comportamiento previo (S7). El snapshot
    // persistido solo se usa como fallback sin USD o sin tasa válida.
    private decimal BsSFromRateOrFallback(decimal usdAmount, decimal fallbackBsS)
        => usdAmount > 0 && _exchangeRateService.CurrentRate > 0
            ? Helpers.EffectivePriceVesHelper.EffectiveBsS(usdAmount, _exchangeRateService.CurrentRate, EffectiveCashRoundingRule, _neverRoundDownCashBs)
            : fallbackBsS;

    /// <summary>Regla cash efectiva del artículo: override propio ?? política global (S1).</summary>
    private RoundingRuleType? EffectiveCashRoundingRule => _dto.RoundingRuleCashBs ?? _globalCashRoundingRule;

    public string DisplayRetailPrice
    {
        get
        {
            if (IsGroupHeader && HasIndependentPricing) return "—";
            if (SelectedCurrency == "USD") return $"${CurrencyDisplay.Number(PriceUSD)}";
            decimal bss = BsSFromRateOrFallback(PriceUSD, PriceBsS);
            return $"Bs.S {CurrencyDisplay.Number(bss)}";
        }
    }

    public string DisplayWholesalePrice
    {
        get
        {
            if (IsGroupHeader && HasIndependentPricing) return "—";
            if (SelectedCurrency == "USD") return $"${CurrencyDisplay.Number(EffectivePriceWholesaleUSD)}";
            decimal bss = BsSFromRateOrFallback(EffectivePriceWholesaleUSD, PriceBsS);
            return $"Bs.S {CurrencyDisplay.Number(bss)}";
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
        _priceBsS = dto.PriceBsS > 0 ? dto.PriceBsS : (_exchangeRateService.CurrentRate > 0 ? Helpers.PricingHelper.ToBsSCeiling(dto.PriceUSD, _exchangeRateService.CurrentRate) : 0m);
        
        _priceWholesaleUSD = dto.PriceWholesaleUSD;
        _priceWholesaleBsS = dto.PriceWholesaleUSD > 0 
            ? (_exchangeRateService.CurrentRate > 0 ? Helpers.PricingHelper.ToBsSCeiling(dto.PriceWholesaleUSD, _exchangeRateService.CurrentRate) : 0m) 
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

    // 8.143: re-notifica los displays formateados cuando el ajuste de moneda cambia en caliente.
    public void NotifyCurrencyFormatChanged()
    {
        OnPropertyChanged(nameof(DisplayCost));
        OnPropertyChanged(nameof(DisplayRetailPrice));
        OnPropertyChanged(nameof(DisplayWholesalePrice));
        OnPropertyChanged(nameof(EffectivePriceWholesaleBsS));
    }

    /// <summary>
    /// 8.185-T4 (S2): aplica la política global vigente (regla cash y toggle de piso) y re-notifica
    /// el Bs.S efectivo para que la grilla refleje el mismo monto que POS y Carrito sin consultas.
    /// </summary>
    public void ApplyPricingPolicies(RoundingRuleType? globalCashRoundingRule, bool neverRoundDownCashBs)
    {
        _globalCashRoundingRule = globalCashRoundingRule;
        _neverRoundDownCashBs = neverRoundDownCashBs;
        OnPropertyChanged(nameof(EffectivePriceWholesaleBsS));
        OnPropertyChanged(nameof(DisplayRetailPrice));
        OnPropertyChanged(nameof(DisplayWholesalePrice));
    }

    public void UpdateExchangeRate()
    {
        if (_isCalculating) return;

        // 8.143 + 8.185 (S1): no pisar precios Bs.S-only con 0; con USD y tasa válida, el Bs.S
        // efectivo se recalcula con el helper único. Este campo alimenta solo el fallback de
        // display: la persistencia sigue anclada al Bs.S canónico 8.104 (_dto.PriceBsS).
        if (PriceUSD > 0 && _exchangeRateService.CurrentRate > 0)
        {
            PriceBsS = Helpers.EffectivePriceVesHelper.EffectiveBsS(PriceUSD, _exchangeRateService.CurrentRate, EffectiveCashRoundingRule, _neverRoundDownCashBs);
        }

        if (PriceWholesaleUSD > 0 && _exchangeRateService.CurrentRate > 0)
        {
            PriceWholesaleBsS = Helpers.EffectivePriceVesHelper.EffectiveBsS(PriceWholesaleUSD, _exchangeRateService.CurrentRate, EffectiveCashRoundingRule, _neverRoundDownCashBs);
        }

        OnPropertyChanged(nameof(EffectivePriceWholesaleBsS));
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
                : (_exchangeRateService.CurrentRate > 0 ? Helpers.PricingHelper.ToBsSCeiling(dto.PriceUSD, _exchangeRateService.CurrentRate) : 0m);

            PriceWholesaleUSD = dto.PriceWholesaleUSD;
            PriceWholesaleBsS = dto.PriceWholesaleUSD > 0 
                ? (_exchangeRateService.CurrentRate > 0 ? Helpers.PricingHelper.ToBsSCeiling(dto.PriceWholesaleUSD, _exchangeRateService.CurrentRate) : 0m) 
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
            PriceUSD = Core.Helpers.PricingCalculator.CalculateSuggestedPrice(Cost, value);
            PriceBsS = Helpers.PricingHelper.ToBsSCeiling(PriceUSD, _exchangeRateService.CurrentRate);
            
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
