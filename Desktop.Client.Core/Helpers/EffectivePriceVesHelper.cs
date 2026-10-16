using System.Runtime.CompilerServices;
using Core.DTOs;
using Core.Helpers;

namespace Desktop.Client.Helpers;

/// <summary>
/// 8.185-T4 (S1/S7): ÚNICO punto de cálculo en memoria del precio efectivo unitario en Bs.S del
/// cliente. Con regla cash efectiva delega en el motor geométrico compartido
/// (<see cref="PricingRoundingPolicy.ProjectCashFriendlyBsS(decimal, decimal, RoundingRuleType, bool)"/>,
/// decisión A: toggle ON ⇒ nunca-hacia-abajo) y con regla null/ExactMath o tasa no válida conserva
/// el techo canónico 8.104 (<see cref="PricingCalculator.ToBsSCeiling"/>), byte-idéntico al
/// comportamiento previo (S7). Catálogo, Modal de Producto, Buscador del POS y (T5) Carrito leen
/// de acá, de modo que el mismo USD × tasa produce el mismo Bs.S en todas las pantallas.
/// Garantía anti-trinquete 8.183: el flujo es unidireccional USD × tasa → Bs.S efectivo; este
/// cálculo nunca muta PriceUSD, márgenes ni el Bs.S canónico persistido.
/// </summary>
public static class EffectivePriceVesHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal EffectiveBsS(decimal priceUsd, decimal rate, RoundingRuleType? cashRule, bool neverRoundDown)
    {
        // Sin regla cash explícita o sin tasa convertible rige el techo canónico (S7).
        if (cashRule is null or RoundingRuleType.ExactMath || rate <= 0m)
        {
            return PricingCalculator.ToBsSCeiling(priceUsd, rate);
        }

        // El motor devuelve null solo fuera de contrato (USD ≤ 0 o tasa ≤ 0, ya cubierta la tasa);
        // el fallback conserva la garantía de retorno decimal con la conversión canónica.
        return PricingRoundingPolicy
            .ProjectCashFriendlyBsS(priceUsd, rate, cashRule.Value, neverRoundDown)
            ?.RoundedValue ?? PricingCalculator.ToBsSCeiling(priceUsd, rate);
    }
}
