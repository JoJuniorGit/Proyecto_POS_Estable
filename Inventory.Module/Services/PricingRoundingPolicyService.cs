using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;

namespace Inventory.Module.Services;

/// <summary>
/// 8.182-T1 (S1/S2): servicio puro de redondeo comercial/psicológico con guarda de piso de margen.
/// Stateless y sin dependencias de persistencia: la misma instancia puede usarse desde el motor
/// bulk, las facturas de proveedor o hooks futuros del POS sin efectos colaterales.
/// 8.184-T1 (S3): el algoritmo se relocalizó a <see cref="PricingRoundingPolicy"/> (Core) para que
/// el cliente WPF calcule en vivo sin acoplarse a Inventory.Module; este servicio solo delega y
/// mantiene intacto el contrato IPricingRoundingPolicyService para los consumidores existentes.
/// </summary>
public sealed class PricingRoundingPolicyService : IPricingRoundingPolicyService
{
    public RoundingResult Round(decimal amount, RoundingRuleType rule, decimal cost, decimal? marginFloorPct, bool enforceFloor)
        => PricingRoundingPolicy.Round(amount, rule, cost, marginFloorPct, enforceFloor);

    public decimal? FindLowerStep(decimal amount, RoundingRuleType rule)
        => PricingRoundingPolicy.FindLowerStep(amount, rule);

    public RoundingResult? ProjectCashFriendlyBsS(
        decimal usdAmount,
        decimal exchangeRate,
        RoundingRuleType rule,
        decimal costUsd,
        decimal? marginFloorPct,
        bool enforceFloor)
        => PricingRoundingPolicy.ProjectCashFriendlyBsS(usdAmount, exchangeRate, rule, costUsd, marginFloorPct, enforceFloor);

    /// <summary>
    /// 8.186-T1 (S1/S3): proyección del precio efectivo Bs.S que consumen las superficies de lectura
    /// server-side (catálogo web, buscador, quick-check y variantes). Misma semántica que el helper
    /// único del cliente WPF (Desktop.Client.Helpers.EffectivePriceVesHelper): con regla cash efectiva
    /// delega en el kernel geométrico (<see cref="PricingRoundingPolicy.ProjectCashFriendlyBsS(decimal, decimal, RoundingRuleType, bool)"/>,
    /// neverRoundDown = toggle de piso); con regla null/ExactMath o tasa no válida conserva el techo
    /// canónico 8.104 (<see cref="PricingCalculator.ToBsSCeiling"/>) — byte-idéntico al canónico de hoy.
    /// </summary>
    public static decimal ProjectEffectiveBsS(decimal priceUsd, decimal rate, RoundingRuleType? cashRule, bool neverRoundDown)
    {
        // Sin regla cash explícita o sin tasa convertible rige el techo canónico (paridad 8.143).
        if (cashRule is null or RoundingRuleType.ExactMath || rate <= 0m)
        {
            return PricingCalculator.ToBsSCeiling(priceUsd, rate);
        }

        // El kernel devuelve null solo fuera de contrato (USD ≤ 0 o tasa ≤ 0, ya cubierta la tasa);
        // el fallback conserva la garantía de retorno decimal con la conversión canónica.
        return PricingRoundingPolicy
            .ProjectCashFriendlyBsS(priceUsd, rate, cashRule.Value, neverRoundDown)
            ?.RoundedValue ?? PricingCalculator.ToBsSCeiling(priceUsd, rate);
    }
}
