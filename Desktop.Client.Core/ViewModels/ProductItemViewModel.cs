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
    private decimal _cost;

    [ObservableProperty]
    private int _stockQuantity;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isDeleted;

    [ObservableProperty]
    private decimal _priceWholesaleUSD;

    [ObservableProperty]
    private decimal _priceWholesaleBsS;

    [ObservableProperty]
    private decimal _minWholesaleQuantity;

    [ObservableProperty]
    private bool _hasWholesale;

    [ObservableProperty]
    private string _unitOfMeasureStr = "Und";

    [ObservableProperty]
    private string _selectedCurrency = "Bs.S";

    public bool IsInactive => !IsActive && !IsDeleted;

    public string StatusLabel => IsDeleted ? "ELIMINADO (HISTORIAL)" : (!IsActive ? "INACTIVO" : "ACTIVO");

    public bool HasRealWholesale => (HasWholesale || PriceWholesaleUSD > 0) && PriceWholesaleUSD > 0 && PriceWholesaleUSD < PriceUSD;

    public decimal EffectivePriceWholesaleUSD => HasRealWholesale ? PriceWholesaleUSD : PriceUSD;

    public decimal EffectivePriceWholesaleBsS => HasRealWholesale 
        ? (PriceWholesaleUSD > 0 ? Math.Round(PriceWholesaleUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero) : PriceBsS) 
        : PriceBsS;

    public decimal EffectiveMinWholesaleQuantity => HasRealWholesale ? (MinWholesaleQuantity > 0 ? MinWholesaleQuantity : 1m) : 1m;

    public bool IsStockCritical => StockQuantity <= 0;

    public string FormattedMinWholesaleQuantityStr => ((int)Math.Round(EffectiveMinWholesaleQuantity, MidpointRounding.AwayFromZero)).ToString();

    public string WholesalePriceColor => HasRealWholesale ? "#6366F1" : "#D97706";


    public string DisplayRetailPrice
    {
        get
        {
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
            if (SelectedCurrency == "USD") return $"${EffectivePriceWholesaleUSD:N2}";
            decimal bss = EffectivePriceWholesaleBsS > 0 
                ? EffectivePriceWholesaleBsS 
                : (_exchangeRateService.CurrentRate > 0 ? Math.Round(EffectivePriceWholesaleUSD * _exchangeRateService.CurrentRate, 2, MidpointRounding.AwayFromZero) : 0m);
            return $"Bs.S {bss:N2}";
        }
    }

    public ProductItemViewModel(ProductDto dto, Services.IExchangeRateService exchangeRateService, Action<ProductItemViewModel> onChanged)
    {
        _dto = dto;
        _exchangeRateService = exchangeRateService;
        _onChanged = onChanged;
        
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

        _isActive = dto.IsActive;
        _isDeleted = dto.IsDeleted;
    }

    public void NotifyCurrencyChanged(string newCurrency)
    {
        SelectedCurrency = newCurrency;
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

            IsActive = dto.IsActive;
            IsDeleted = dto.IsDeleted;

            OnPropertyChanged(nameof(IsInactive));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(HasRealWholesale));
            OnPropertyChanged(nameof(EffectiveMinWholesaleQuantity));
            OnPropertyChanged(nameof(FormattedMinWholesaleQuantityStr));
            OnPropertyChanged(nameof(IsStockCritical));
            OnPropertyChanged(nameof(WholesalePriceColor));
            OnPropertyChanged(nameof(DisplayRetailPrice));
            OnPropertyChanged(nameof(DisplayWholesalePrice));

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
