using System;
using Core.Common;

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
    /// Rounds an exchange rate up to 2 decimal places (ceiling rounding), alineado a la guia §2.4.
    /// Example: 804.63001 -> 804.64, 804.64 -> 804.64 (identidad al tope de precision).
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
    /// Converts a USD amount to Bs.S rounding UP to 2 decimal places (ceiling).
    /// Estandar 8.104: el precio unitario en Bs.S se redondea siempre hacia arriba (0.81 * 842.21 = 682.1901 -> 682.20)
    /// y es la unica fuente de verdad en catalogo, carrito, caja, facturacion y recibos.
    /// </summary>
    public static decimal ToBsSCeiling(decimal amountUsd, decimal rate)
    {
        if (rate <= 0) return 0m;
        return RoundPriceUp(amountUsd * rate);
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

    /// <summary>
    /// 8.169 [CLEAN-01]: canonical calculation of a supplier invoice line unit cost in USD,
    /// dividing the document unit cost by the effective box-to-unit conversion factor (rounded to
    /// 4 decimals when factor != 1) and converting from Bs.S using the invoice snapshot rate.
    /// </summary>
    public static decimal CalculateInvoiceUnitCostUsd(
        decimal unitCostDocument,
        decimal conversionFactor,
        string? currency,
        decimal appliedRate)
    {
        decimal effectiveFactor = conversionFactor > 0m ? conversionFactor : 1m;
        decimal unitDocumentCost = effectiveFactor == 1m
            ? unitCostDocument
            : Math.Round(unitCostDocument / effectiveFactor, 4, MidpointRounding.AwayFromZero);

        if (string.Equals(currency, CurrencyCodes.BsS, StringComparison.OrdinalIgnoreCase))
        {
            return ToUSD(unitDocumentCost, appliedRate);
        }

        return unitDocumentCost;
    }

    /// <summary>
    /// [CLEAN-09]: Canonical calculation of a suggested USD selling price from USD cost and profit margin percentage,
    /// rounded to digital precision (2 decimals, AwayFromZero).
    /// </summary>
    public static decimal CalculateSuggestedPrice(decimal costUsd, decimal profitMarginPercent)
    {
        return RoundToDigital(costUsd * (1m + (profitMarginPercent / 100m)));
    }
}
