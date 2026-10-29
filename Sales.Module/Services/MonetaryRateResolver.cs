using Core.Common;
using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// 8.211-T3c3e (S7): single authority for read-only projections that need the active profile's
/// rate (credit portfolio/statement, credit-payment receipt). Single is identity; Dual USD/VES
/// stays BCV; Dual USD/{COP,MXN,EUR} uses the exact daily manual quote. A non-VES profile never
/// falls back to BCV: a missing profile, service or quote fails closed. Without a profile service
/// the historical BCV-provider behavior is preserved for direct legacy compositions.
/// </summary>
public static class MonetaryRateResolver
{
    private static readonly HashSet<string> ManualProjectionCurrencies = new(StringComparer.Ordinal)
    {
        "COP",
        "MXN",
        "EUR"
    };

    private const string UsdCode = "USD";
    private const string VesCode = "VES";

    public static async Task<decimal> ResolveTodayProjectionRateAsync(
        IMonetaryProfileService? monetaryProfileService,
        ICurrencyRateService? currencyRateService,
        ITodayExchangeRateProvider? bcvProvider,
        string contextLabel,
        CancellationToken cancellationToken)
    {
        if (monetaryProfileService is null)
        {
            return await ResolveLegacyTodayRateAsync(bcvProvider, cancellationToken);
        }

        MonetaryProfileDto profile;
        try
        {
            profile = await monetaryProfileService.GetAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"No se pudo leer el perfil monetario para proyectar {contextLabel}. Verifique la configuración monetaria.",
                ex);
        }

        if (profile.Mode == MonetaryMode.Single)
        {
            return 1m;
        }

        if (profile.Mode != MonetaryMode.Dual)
        {
            throw new InvalidOperationException(
                $"El perfil monetario actual (modo {profile.Mode}) no está habilitado para proyectar {contextLabel}.");
        }

        var primary = CurrencyCodeNormalizer.NormalizeToIso(profile.PrimaryCurrencyCode);
        var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode);
        if (!string.Equals(primary, UsdCode, StringComparison.Ordinal) || secondary is null)
        {
            throw new InvalidOperationException(
                $"El perfil monetario actual ({profile.PrimaryCurrencyCode}/{profile.SecondaryCurrencyCode}) no está habilitado para proyectar {contextLabel}: se requiere Dual USD con una secundaria configurada.");
        }

        if (string.Equals(secondary, VesCode, StringComparison.Ordinal))
        {
            return await ResolveLegacyTodayRateAsync(bcvProvider, cancellationToken);
        }

        if (!ManualProjectionCurrencies.Contains(secondary))
        {
            throw new InvalidOperationException(
                $"El perfil monetario actual (USD/{secondary}) no tiene una fuente de tasa soportada para proyectar {contextLabel}.");
        }

        if (currencyRateService is null)
        {
            throw new InvalidOperationException(
                $"No hay servicio de tasas manuales configurado para el par USD/{secondary} para proyectar {contextLabel}.");
        }

        var quote = await currencyRateService.GetRateAsync(UsdCode, secondary, null, cancellationToken);
        if (quote.Rate <= 0m)
        {
            throw new InvalidOperationException(
                $"La tasa manual del par USD/{secondary} no es válida para proyectar {contextLabel}.");
        }

        return PricingCalculator.RoundExchangeRate(quote.Rate, PricingCalculator.ManualRateDecimals);
    }

    private static async Task<decimal> ResolveLegacyTodayRateAsync(
        ITodayExchangeRateProvider? bcvProvider,
        CancellationToken cancellationToken)
        => bcvProvider != null
            ? await bcvProvider.GetEffectiveTodayRateAsync(cancellationToken)
            : 0m;
}
