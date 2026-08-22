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
    /// Converts a Bs.S amount to USD using the provided exchange rate and digital rounding.
    /// </summary>
    public static decimal ToUSD(decimal amountBsS, decimal rate)
    {
        if (rate <= 0) return 0m;
        return RoundToDigital(amountBsS / rate);
    }
}
