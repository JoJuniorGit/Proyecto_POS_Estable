using System;

namespace Core.Helpers;

/// <summary>
/// Centralized calculation engine for pricing, currency conversion, and precision rounding.
/// Shared across backend and frontend layers to ensure zero calculation drift.
/// </summary>
public static class PricingCalculator
{
    public const int DigitalDecimals = 2;
    public const int CashDecimals = 0;

    /// <summary>
    /// Rounds an amount to digital precision (2 decimal places) using MidpointRounding.AwayFromZero.
    /// Used for electronic payments, bank transfers, credit cards, and ledger totals.
    /// </summary>
    public static decimal RoundToDigital(decimal amount)
    {
        return Math.Round(amount, DigitalDecimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Rounds an exchange rate up to 2 decimal places (ceiling rounding).
    /// Example: 804.6301 -> 804.64, 804.6300 -> 804.63.
    /// </summary>
    public static decimal RoundExchangeRateCeiling(decimal rate)
    {
        return Math.Ceiling(rate * 100m) / 100m;
    }

    /// <summary>
    /// Rounds a monetary value up to 2 decimal places (ceiling rounding).
    /// Used para precios calculados desde costo+margen (nunca se redondea hacia abajo).
    /// </summary>
    public static decimal RoundPriceUp(decimal amount)
    {
        return Math.Ceiling(amount * 100m) / 100m;
    }

    /// <summary>
    /// Rounds an amount to cash precision (0 decimal places / integer).
    /// Chained from digital rounding to guarantee consistency.
    /// </summary>
    public static decimal RoundToCash(decimal amount)
    {
        decimal digitalValue = RoundToDigital(amount);
        return Math.Round(digitalValue, CashDecimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Converts a USD amount to Bs.S using the provided exchange rate and digital rounding.
    /// </summary>
    public static decimal ToBsS(decimal amountUsd, decimal rate)
    {
        if (rate <= 0) return 0m;
        return RoundToDigital(amountUsd * rate);
    }

    /// <summary>
    /// Converts a Bs.S amount to USD using the provided exchange rate.
    /// Defaults to 2 decimals for digital presentation, supports 4 decimals for high-precision currency conversions [8C-M1].
    /// </summary>
    public static decimal ToUSD(decimal amountBsS, decimal rate, int decimals = 2)
    {
        if (rate <= 0) return 0m;
        return Math.Round(amountBsS / rate, decimals, MidpointRounding.AwayFromZero);
    }
}
