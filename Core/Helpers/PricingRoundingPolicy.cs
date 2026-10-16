using System;
using System.Runtime.CompilerServices;
using Core.DTOs;
using Core.Interfaces;

namespace Core.Helpers;

/// <summary>
/// 8.184-T1 (S3): algoritmo puro de redondeo comercial/psicológico con guarda de piso de margen,
/// relocalizado tal cual desde Inventory.Module.Services.PricingRoundingPolicyService para que
/// cualquier capa (backend y cliente WPF) comparta el motor sin acoplarse a Inventory.Module.
/// El servicio conserva el contrato IPricingRoundingPolicyService delegando en estos métodos
/// estáticos; consumidores y tests existentes quedan intactos.
/// 8.185-perf (T-opt): ruta de efectivo Bs.S unificada (techo directo con piso activo ⇒ paridad
/// exacta 6-args ≡ 4-args) y result como readonly record struct — cero allocations en la ruta
/// caliente (Catálogo, Modal, POS, Carrito, venta y bulk).
/// </summary>
public static class PricingRoundingPolicy
{
    // Escalera psicológica: terminaciones en centavos dentro de la unidad (X.25 / X.50 / X.75 / X.95 / X.99).
    private static readonly decimal[] PsychologicalFractionCents = { 25m, 50m, 75m, 95m, 99m };

    public static RoundingResult Round(decimal amount, RoundingRuleType rule, decimal cost, decimal? marginFloorPct, bool enforceFloor)
    {
        EnsureKnownRule(rule);

        // Un importe no positivo no participa de la escalera (el motor nunca produce precios ≤ 0).
        // ExactMath es passthrough: sin escalones no hay redondeo ni guarda de piso aplicable.
        if (amount <= 0m || rule == RoundingRuleType.ExactMath)
        {
            return new RoundingResult(amount, amount, 0m, CalculateEffectiveMarginPct(amount, cost));
        }

        decimal rounded;
        if (rule == RoundingRuleType.UsdPsychological)
        {
            rounded = RoundToNearestPsychological(amount);

            // S2: el piso se evalúa sobre el precio FINAL. Si el escalón elegido (inferior o neutro
            // respecto del matemático) deja el margen efectivo por debajo del piso, se usa el escalón
            // inmediato superior. Un escalón ya superior al matemático no se recorre de nuevo.
            if (enforceFloor && marginFloorPct is > 0m && cost > 0m && rounded <= amount)
            {
                decimal? margin = CalculateEffectiveMarginPct(rounded, cost);
                if (margin.HasValue && margin.Value < marginFloorPct.Value)
                {
                    decimal? next = NextStepUp(rounded, rule);
                    if (next.HasValue)
                    {
                        rounded = next.Value;
                    }
                }
            }
        }
        else
        {
            // 8.185-perf (T-opt): reglas de efectivo Bs.S (Entero/×5/×10). Con la protección activa
            // (enforceFloor = toggle EnforceMinimumMarginFloor) se aplica TECHO DIRECTO sobre la cota
            // mínima: reemplaza el nearest + verificación reactiva de margen a 2dp + cadena de bump
            // (NextStepUp), elimina el déficit sub-tolerancia de 0,005 pp y logra paridad exacta
            // 6-args ≡ 4-args (venta) en el 100% de los casos. Sin protección ⇒ escalón más cercano.
            // Nota de semántica: en reglas VES el VALOR del piso no cambia la dirección — el toggle
            // ES la protección (consistent con la ruta de venta, que no dispone de piso numérico).
            rounded = RoundCashStep(amount, rule, roundUpOnly: enforceFloor);
        }

        return new RoundingResult(amount, rounded, rounded - amount, CalculateEffectiveMarginPct(rounded, cost));
    }

    public static decimal? FindLowerStep(decimal amount, RoundingRuleType rule)
    {
        EnsureKnownRule(rule);

        if (amount <= 0m)
        {
            return null;
        }

        if (rule == RoundingRuleType.UsdPsychological)
        {
            decimal cents = amount * 100m;
            decimal wholeCents = Math.Floor(cents / 100m) * 100m;
            decimal? lowerCents = null;
            foreach (var fraction in PsychologicalFractionCents)
            {
                decimal candidate = wholeCents + fraction;
                if (candidate < cents)
                {
                    lowerCents = candidate;
                }
            }

            // Fracción menor a .25 en la unidad actual: el escalón inferior es el .99 anterior.
            if (!lowerCents.HasValue && wholeCents > 0m)
            {
                lowerCents = wholeCents - 1m;
            }

            return lowerCents.HasValue ? lowerCents.Value / 100m : null;
        }

        // ExactMath y las reglas VES (que operan en Bs.S, no en USD) bajan con resolución de centavo.
        decimal previous = Math.Round(amount - 0.01m, 2, MidpointRounding.AwayFromZero);
        return previous > 0m ? previous : null;
    }

    public static RoundingResult? ProjectCashFriendlyBsS(
        decimal usdAmount,
        decimal exchangeRate,
        RoundingRuleType rule,
        decimal costUsd,
        decimal? marginFloorPct,
        bool enforceFloor)
    {
        EnsureKnownRule(rule);

        // Sin importe/tasa convertibles o con una regla que no es de efectivo no hay proyección.
        if (!IsCashFriendlyRule(rule) || usdAmount <= 0m || exchangeRate <= 0m)
        {
            return null;
        }

        // 8.185-perf (T-opt): reglas VES con piso activo ⇒ techo directo sobre la cota mínima
        // (mismo kernel que la ruta de venta 4-args; el margen a 2dp ya no interviene para cash);
        // UsdPsychological conserva el piso reactivo por margen del bloque de Round.
        return Round(usdAmount * exchangeRate, rule, costUsd * exchangeRate, marginFloorPct, enforceFloor);
    }

    /// <summary>
    /// 8.185-T1 (S3) + 8.185-perf (T-opt): proyección geométrica del Bs.S de efectivo SIN costo ni
    /// margen (decisión A). Con <paramref name="neverRoundDown"/> (toggle ON) aplica techo directo
    /// sobre la cota mínima — nunca por debajo del valor matemático, misma aritmética que el 6-args
    /// con piso activo (paridad 100%). OFF = más cercano (puede bajar). Los guards de 8.183 se
    /// conservan: regla no-cash, USD ≤ 0 o tasa ≤ 0 → null.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RoundingResult? ProjectCashFriendlyBsS(
        decimal usdAmount,
        decimal exchangeRate,
        RoundingRuleType rule,
        bool neverRoundDown)
    {
        EnsureKnownRule(rule);

        // Sin importe/tasa convertibles o con una regla que no es de efectivo no hay proyección.
        if (!IsCashFriendlyRule(rule) || usdAmount <= 0m || exchangeRate <= 0m)
        {
            return null;
        }

        // 8.185-perf (T-opt): mismo kernel que el 6-args — techo directo (toggle ON) o más cercano
        // (OFF), sin llamada intermedia a Round ni cadena de bump: cero allocations adicionales y
        // paridad matemática exacta con la ruta de edición. Sin costo ⇒ sin EffectiveMarginPct.
        decimal mathematical = usdAmount * exchangeRate;
        decimal rounded = RoundCashStep(mathematical, rule, roundUpOnly: neverRoundDown);
        return new RoundingResult(mathematical, rounded, rounded - mathematical, null);
    }

    // ---------------------------------------------------------------------------------------------
    // Escaleras.
    // ---------------------------------------------------------------------------------------------

    private static decimal RoundToNearestPsychological(decimal amount)
    {
        decimal cents = amount * 100m;
        decimal wholeCents = Math.Floor(cents / 100m) * 100m;

        decimal? lowerCents = null;
        foreach (var fraction in PsychologicalFractionCents)
        {
            decimal candidate = wholeCents + fraction;
            if (candidate <= cents)
            {
                lowerCents = candidate;
            }
        }

        if (!lowerCents.HasValue && wholeCents > 0m)
        {
            // Fracción < .25: el vecino inferior es el .99 de la unidad anterior.
            lowerCents = wholeCents - 1m;
        }

        decimal? upperCents = null;
        foreach (var fraction in PsychologicalFractionCents)
        {
            decimal candidate = wholeCents + fraction;
            if (candidate >= cents)
            {
                upperCents = candidate;
                break;
            }
        }

        if (!upperCents.HasValue)
        {
            // Parte decimal por encima de .99 (posible con más de 2 decimales): siguiente unidad.
            upperCents = wholeCents + 100m + PsychologicalFractionCents[0];
        }

        // Empate → escalón inferior (el piso de margen puede rechazarlo y forzar el superior).
        if (!lowerCents.HasValue || cents - lowerCents.Value <= upperCents.Value - cents)
        {
            return (lowerCents ?? upperCents.Value) / 100m;
        }

        return upperCents.Value / 100m;
    }

    // 8.185-perf (T-opt): paso de la regla VES (1/5/10) — mapeo único regla→escalón.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static decimal CashStepOf(RoundingRuleType rule) =>
        rule == RoundingRuleType.VesCashFriendlyInteger ? 1m
        : rule == RoundingRuleType.VesCashFriendlyStep5 ? 5m
        : 10m;

    // 8.185-perf (T-opt): kernel único del redondeo de efectivo Bs.S de la ruta caliente.
    // roundUpOnly (toggle ON / piso activo) ⇒ techo directo sobre la cota mínima (nunca por debajo,
    // sin costo ni margen a 2dp); OFF ⇒ escalón más cercano (puede bajar). Reemplaza nearest+bump.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static decimal RoundCashStep(decimal amount, RoundingRuleType rule, bool roundUpOnly)
    {
        decimal step = CashStepOf(rule);
        return roundUpOnly ? Math.Ceiling(amount / step) * step : RoundToNearestStep(amount, step);
    }

    private static decimal RoundToNearestStep(decimal amount, decimal step)
    {
        decimal lower = Math.Floor(amount / step) * step;
        if (lower <= 0m)
        {
            // El resultado nunca es ≤ 0: sin escalón inferior positivo se usa el superior.
            return lower + step;
        }

        decimal upper = lower + step;
        return amount - lower <= upper - amount ? lower : upper;
    }

    private static decimal? NextStepUp(decimal value, RoundingRuleType rule)
    {
        switch (rule)
        {
            case RoundingRuleType.VesCashFriendlyInteger:
                return value + 1m;
            case RoundingRuleType.VesCashFriendlyStep5:
                return value + 5m;
            case RoundingRuleType.VesCashFriendlyStep10:
                return value + 10m;
            case RoundingRuleType.UsdPsychological:
                decimal cents = value * 100m;
                decimal wholeCents = Math.Floor(cents / 100m) * 100m;
                decimal fractionCents = cents - wholeCents;
                foreach (var fraction in PsychologicalFractionCents)
                {
                    if (fraction > fractionCents)
                    {
                        return (wholeCents + fraction) / 100m;
                    }
                }

                return (wholeCents + 100m + PsychologicalFractionCents[0]) / 100m;
            default:
                return null; // ExactMath: sin escalones de redondeo.
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static decimal? CalculateEffectiveMarginPct(decimal price, decimal cost) =>
        cost > 0m
            ? PricingCalculator.RoundToDigital((price - cost) / cost * 100m)
            : null;

    // 8.183 (R1): reglas de efectivo (Bs.S) — las únicas que proyectan un valor amigable en caliente.
    private static bool IsCashFriendlyRule(RoundingRuleType rule) =>
        rule is RoundingRuleType.VesCashFriendlyInteger
            or RoundingRuleType.VesCashFriendlyStep5
            or RoundingRuleType.VesCashFriendlyStep10;

    private static void EnsureKnownRule(RoundingRuleType rule)
    {
        if (!Enum.IsDefined(rule))
        {
            throw new ArgumentOutOfRangeException(nameof(rule), rule, "Regla de redondeo no reconocida.");
        }
    }
}
