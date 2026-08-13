using System;

namespace Desktop.Client.Helpers;

/// <summary>
/// Central utility for currency conversion and rounding logic.
/// Ensures that digital and cash calculations follow a consistent chained pattern.
/// </summary>
public static class PricingHelper
{
    private const int DigitalDecimals = 2;
    private const int CashDecimals = 0;

    /// <summary>
    /// Rounds an amount to digital precision (2 decimal places).
    /// Used for bank transfers, cards, and general system totals.
    /// </summary>
    public static decimal RoundToDigital(decimal amount)
    {
        return Math.Round(amount, DigitalDecimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Rounds an amount to cash precision (0 decimal places).
    /// IMPORTANT: This is chained from the digital result to ensure a consistent base.
    /// </summary>
    public static decimal RoundToCash(decimal amount)
    {
        // Chain: First round to digital, then round that result to integer
        decimal digitalValue = RoundToDigital(amount);
        return Math.Round(digitalValue, CashDecimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Converts a USD amount to Bs.S Digital using the provided rate and standard rounding.
    /// </summary>
    public static decimal ToBsS(decimal amountUsd, decimal rate)
    {
        if (rate <= 0) return 0;
        return RoundToDigital(amountUsd * rate);
    }
}
