using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.ComponentModel.DataAnnotations;

namespace Desktop.Client.ViewModels;

public partial class ProductDialogViewModel
{
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
    private bool _isSellingAtLoss;

    [ObservableProperty]
    private decimal _priceBsS;

    private bool _isUpdatingPrices;

    public bool IsInheritingPricing => SelectedParentProduct != null && SelectedParentProduct.Id > 0 && !SelectedParentProduct.HasIndependentPricing;
    public bool ShowPricingInputs => !IsCashAdvance && !(IsGroupHeader && HasIndependentPricing);
    public bool ShowIndependentPricingNotice => !IsCashAdvance && IsGroupHeader && HasIndependentPricing;
    public bool CanEditPricing => !IsInheritingPricing && !(IsGroupHeader && HasIndependentPricing);
    public bool CanEditWholesale => HasWholesale && !IsInheritingPricing && !(IsGroupHeader && HasIndependentPricing);

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
}
