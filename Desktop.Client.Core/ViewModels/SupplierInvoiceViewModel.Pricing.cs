using Core.Helpers;

namespace Desktop.Client.ViewModels;

public sealed partial class SupplierInvoiceLineViewModel
{
    private bool _isUpdatingPrices;

    partial void OnMarginRetailOverrideChanged(decimal value) => RecalculateSuggestedPrices();

    partial void OnMarginWholesaleOverrideChanged(decimal value) => RecalculateSuggestedPrices();

    private void RecalculateSuggestedPrices()
    {
        if (_isUpdatingPrices)
        {
            return;
        }

        _isUpdatingPrices = true;
        try
        {
            SuggestedRetailPriceUSD = PricingCalculator.RoundPriceUp(
                UnitCostUSD * (1m + MarginRetailOverride / 100m));
            SuggestedWholesalePriceUSD = PricingCalculator.RoundPriceUp(
                UnitCostUSD * (1m + MarginWholesaleOverride / 100m));
        }
        finally
        {
            _isUpdatingPrices = false;
        }
    }
}
