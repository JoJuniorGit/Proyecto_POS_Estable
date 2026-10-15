using System;
using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;

namespace Inventory.Module.Services;

/// <summary>
/// 8.182-T1 (S1/S2): servicio puro de redondeo comercial/psicológico con guarda de piso de margen.
/// Stateless y sin dependencias de persistencia: la misma instancia puede usarse desde el motor
/// bulk, las facturas de proveedor o hooks futuros del POS sin efectos colaterales.
/// </summary>
public sealed class PricingRoundingPolicyService : IPricingRoundingPolicyService
{
    // Escalera psicológica: terminaciones en centavos dentro de la unidad (X.25 / X.50 / X.75 / X.95 / X.99).
    private static readonly decimal[] PsychologicalFractionCents = { 25m, 50m, 75m, 95m, 99m };

    public RoundingResult Round(decimal amount, RoundingRuleType rule, decimal cost, decimal? marginFloorPct, bool enforceFloor)
    {
        EnsureKnownRule(rule);

        // Un importe no positivo no participa de la escalera (el motor nunca produce precios ≤ 0).
        // ExactMath es passthrough: sin escalones no hay redondeo ni guarda de piso aplicable.
        if (amount <= 0m || rule == RoundingRuleType.ExactMath)
        {
            return new RoundingResult(amount, amount, 0m, CalculateEffectiveMarginPct(amount, cost));
        }

        decimal rounded = rule switch
        {
            RoundingRuleType.UsdPsychological => RoundToNearestPsychological(amount),
            RoundingRuleType.VesCashFriendlyInteger => RoundToNearestStep(amount, 1m),
            RoundingRuleType.VesCashFriendlyStep5 => RoundToNearestStep(amount, 5m),
            _ => RoundToNearestStep(amount, 10m)
        };

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

        return new RoundingResult(amount, rounded, rounded - amount, CalculateEffectiveMarginPct(rounded, cost));
    }

    public decimal? FindLowerStep(decimal amount, RoundingRuleType rule)
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

    private static void EnsureKnownRule(RoundingRuleType rule)
    {
        if (!Enum.IsDefined(rule))
        {
            throw new ArgumentOutOfRangeException(nameof(rule), rule, "Regla de redondeo no reconocida.");
        }
    }
}
