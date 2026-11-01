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
/// 8.211-T3c3e/T3c4 (S7): single authority for the active profile's conversion rate.
/// Single is identity; Dual USD/VES stays on BCV (legacy 2dp ceiling and cash policy); Dual
/// USD/{COP,MXN,EUR} uses the exact daily manual quote with 8dp for calculation and the existing
/// 18,4 columns for the persisted snapshots. A non-VES profile never falls back to BCV: a missing
/// profile, service, or quote fails closed. Without a profile service the historical BCV-provider
/// behavior is preserved for direct legacy compositions.
/// </summary>
public static class MonetaryRateResolver
{
    public const string IdentitySource = "Identity";
    public const string BcvSource = "Bcv";
    public const string ManualSource = "Manual";

    private const string ProjectionAction = "proyectar";
    private const string CalculationAction = "calcular";

    private static readonly HashSet<string> ManualProjectionCurrencies = new(StringComparer.Ordinal)
    {
        "COP",
        "MXN",
        "EUR"
    };

    private const string UsdCode = "USD";
    private const string VesCode = "VES";

    /// <summary>
    /// Rate shaped as the two coordinates financial flows need: the calculation rate (8dp for a
    /// manual pair) and the rate frozen in the persisted 18,4 snapshots (4dp). BCV and Single
    /// return the same value in both coordinates.
    /// </summary>
    public sealed record ResolvedMonetaryRate(decimal CalculationRate, decimal SnapshotRate, string Source);

    /// <summary>
    /// Existing projection entry point (CxC portfolio/statement, credit-payment receipt). Its
    /// observable behavior and messages are preserved: it delegates to the shared core and returns
    /// the calculation rate.
    /// </summary>
    public static async Task<decimal> ResolveTodayProjectionRateAsync(
        IMonetaryProfileService? monetaryProfileService,
        ICurrencyRateService? currencyRateService,
        ITodayExchangeRateProvider? bcvProvider,
        string contextLabel,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveTodayRateCoreAsync(
            monetaryProfileService,
            currencyRateService,
            bcvProvider is null ? null : bcvProvider.GetEffectiveTodayRateAsync,
            ProjectionAction,
            contextLabel,
            cancellationToken);

        return resolved.CalculationRate;
    }

    /// <summary>
    /// Profile-service overload for financial flows that hold <see cref="IMonetaryProfileService"/>
    /// (PettyCash, DailyClosure). The profile is read once and any read failure fails closed: a
    /// mutation can never silently degrade a non-VES profile to Bs.S/BCV.
    /// </summary>
    public static Task<ResolvedMonetaryRate> ResolveTodayRateAsync(
        IMonetaryProfileService? monetaryProfileService,
        ICurrencyRateService? currencyRateService,
        Func<CancellationToken, Task<decimal>>? bcvRateFactory,
        string contextLabel,
        CancellationToken cancellationToken)
        => ResolveTodayRateCoreAsync(
            monetaryProfileService,
            currencyRateService,
            bcvRateFactory,
            CalculationAction,
            contextLabel,
            cancellationToken);

    /// <summary>
    /// BCV delegate overload: lets callers whose BCV source is not an
    /// <see cref="ITodayExchangeRateProvider"/> (PettyCash holds <see cref="IInventoryService"/>)
    /// reuse the same Single/Dual-VES/manual/fail-closed policy without reading the profile twice.
    /// </summary>
    public static Task<ResolvedMonetaryRate> ResolveTodayRateForProfileAsync(
        MonetaryProfileDto? profile,
        ICurrencyRateService? currencyRateService,
        Func<CancellationToken, Task<decimal>>? bcvRateFactory,
        string contextLabel,
        CancellationToken cancellationToken)
        => ResolveTodayRateForProfileCoreAsync(
            profile,
            currencyRateService,
            bcvRateFactory,
            CalculationAction,
            contextLabel,
            cancellationToken);

    /// <summary>
    /// Local drawer currency from the active profile: Single uses the primary and Dual the
    /// normalized secondary (VES presents as Bs.S, any other ISO code as-is). No profile keeps the
    /// legacy Bs.S local currency.
    /// </summary>
    public static string ResolveLocalCurrency(MonetaryProfileDto? profile)
        => profile is null
            ? PaymentMethodCurrencyResolver.LocalCurrency
            : CashDrawerBalanceService.ResolveLocalCurrency(profile);

    /// <summary>
    /// Compares two currency codes through their canonical ISO 4217 form so VES/Bs.S aliases and
    /// persisted rows without normalization classify identically.
    /// </summary>
    public static bool IsSameCurrency(string? left, string? right)
    {
        var leftIso = CurrencyCodeNormalizer.NormalizeToIso(left);
        var rightIso = CurrencyCodeNormalizer.NormalizeToIso(right);

        return leftIso is not null && rightIso is not null
            ? string.Equals(leftIso, rightIso, StringComparison.Ordinal)
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ResolvedMonetaryRate> ResolveTodayRateCoreAsync(
        IMonetaryProfileService? monetaryProfileService,
        ICurrencyRateService? currencyRateService,
        Func<CancellationToken, Task<decimal>>? bcvRateFactory,
        string actionVerb,
        string contextLabel,
        CancellationToken cancellationToken)
    {
        if (monetaryProfileService is null)
        {
            return await ResolveBcvRateAsync(bcvRateFactory, cancellationToken);
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
                $"No se pudo leer el perfil monetario para {actionVerb} {contextLabel}. Verifique la configuración monetaria.",
                ex);
        }

        return await ResolveTodayRateForProfileCoreAsync(
            profile,
            currencyRateService,
            bcvRateFactory,
            actionVerb,
            contextLabel,
            cancellationToken);
    }

    private static async Task<ResolvedMonetaryRate> ResolveTodayRateForProfileCoreAsync(
        MonetaryProfileDto? profile,
        ICurrencyRateService? currencyRateService,
        Func<CancellationToken, Task<decimal>>? bcvRateFactory,
        string actionVerb,
        string contextLabel,
        CancellationToken cancellationToken)
    {
        if (profile is null)
        {
            return await ResolveBcvRateAsync(bcvRateFactory, cancellationToken);
        }

        if (profile.Mode == MonetaryMode.Single)
        {
            return new ResolvedMonetaryRate(1m, 1m, IdentitySource);
        }

        if (profile.Mode != MonetaryMode.Dual)
        {
            throw new InvalidOperationException(
                $"El perfil monetario actual (modo {profile.Mode}) no está habilitado para {actionVerb} {contextLabel}.");
        }

        var primary = CurrencyCodeNormalizer.NormalizeToIso(profile.PrimaryCurrencyCode);
        var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode);
        if (!string.Equals(primary, UsdCode, StringComparison.Ordinal) || secondary is null)
        {
            throw new InvalidOperationException(
                $"El perfil monetario actual ({profile.PrimaryCurrencyCode}/{profile.SecondaryCurrencyCode}) no está habilitado para {actionVerb} {contextLabel}: se requiere Dual USD con una secundaria configurada.");
        }

        if (string.Equals(secondary, VesCode, StringComparison.Ordinal))
        {
            return await ResolveBcvRateAsync(bcvRateFactory, cancellationToken);
        }

        if (!ManualProjectionCurrencies.Contains(secondary))
        {
            throw new InvalidOperationException(
                $"El perfil monetario actual (USD/{secondary}) no tiene una fuente de tasa soportada para {actionVerb} {contextLabel}.");
        }

        if (currencyRateService is null)
        {
            throw new InvalidOperationException(
                $"No hay servicio de tasas manuales configurado para el par USD/{secondary} para {actionVerb} {contextLabel}.");
        }

        var quote = await currencyRateService.GetRateAsync(UsdCode, secondary, null, cancellationToken);
        if (quote.Rate <= 0m)
        {
            throw new InvalidOperationException(
                $"La tasa manual del par USD/{secondary} no es válida para {actionVerb} {contextLabel}.");
        }

        return new ResolvedMonetaryRate(
            PricingCalculator.RoundExchangeRate(quote.Rate, PricingCalculator.ManualRateDecimals),
            PricingCalculator.RoundExchangeRate(quote.Rate, PricingCalculator.RateSnapshotDecimals),
            ManualSource);
    }

    /// <summary>
    /// BCV path shared by legacy compositions and Dual USD/VES. The provider already returns the
    /// 2dp reference in production; the ceiling is applied here so the policy is explicit and the
    /// snapshot never exceeds the legacy scale.
    /// </summary>
    private static async Task<ResolvedMonetaryRate> ResolveBcvRateAsync(
        Func<CancellationToken, Task<decimal>>? bcvRateFactory,
        CancellationToken cancellationToken)
    {
        decimal rate = bcvRateFactory is null ? 0m : await bcvRateFactory(cancellationToken);
        if (rate <= 0m)
        {
            return new ResolvedMonetaryRate(0m, 0m, BcvSource);
        }

        decimal reference = PricingCalculator.RoundExchangeRateCeiling(rate);
        return new ResolvedMonetaryRate(reference, reference, BcvSource);
    }
}
