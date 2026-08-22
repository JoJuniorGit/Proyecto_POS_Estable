using System;
using Core.Helpers;

namespace Desktop.Client.Helpers;

/// <summary>
/// Client helper for currency conversion and rounding logic.
/// Delegates to the shared Core.Helpers.PricingCalculator.
/// </summary>
public static class PricingHelper
{
    public static decimal RoundToDigital(decimal amount) => PricingCalculator.RoundToDigital(amount);

    public static decimal RoundToCash(decimal amount) => PricingCalculator.RoundToCash(amount);

    public static decimal ToBsS(decimal amountUsd, decimal rate) => PricingCalculator.ToBsS(amountUsd, rate);

    public static decimal ToUSD(decimal amountBsS, decimal rate) => PricingCalculator.ToUSD(amountBsS, rate);
}

