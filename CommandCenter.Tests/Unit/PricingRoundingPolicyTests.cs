using System;
using Core.DTOs;
using Core.Helpers;
using Inventory.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.182-T1 (S1/S2): servicio puro de redondeo comercial/psicológico con guarda de piso de margen.
// Cubre: ExactMath passthrough; escalera psicológica bidireccional (incl. < $1 y empates);
// escalones VES 1/5/10 (incl. tasas altas); piso de margen sobre el precio FINAL; costo 0;
// importes no positivos; escalón inmediato inferior (salvaguarda Detal/Mayor).
public class PricingRoundingPolicyTests
{
    private static readonly PricingRoundingPolicyService Policy = new();

    // ---------------------------------------------------------------------------------------------
    // ExactMath.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Round_ExactMath_IsPassthroughAndComputesEffectiveMargin()
    {
        var result = Policy.Round(1.37m, RoundingRuleType.ExactMath, 1.00m, 30m, true);

        Assert.Equal(1.37m, result.MathematicalValue);
        Assert.Equal(1.37m, result.RoundedValue);
        Assert.Equal(0m, result.RoundingDelta);
        Assert.Equal(37.00m, result.EffectiveMarginPct);
    }

    [Fact]
    public void Round_ExactMath_WithNonPositiveCost_HasNoEffectiveMargin()
    {
        var result = Policy.Round(1.37m, RoundingRuleType.ExactMath, 0m, 30m, true);

        Assert.Equal(1.37m, result.RoundedValue);
        Assert.Null(result.EffectiveMarginPct);
    }

    // ---------------------------------------------------------------------------------------------
    // UsdPsychological: terminación más cercana de {.99,.95,.75,.50,.25} bidireccional.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1.37, 1.25)] // 12c vs 13c
    [InlineData(1.49, 1.50)] // 24c vs 1c
    [InlineData(0.81, 0.75)] // precios < $1
    [InlineData(0.90, 0.95)]
    [InlineData(2.60, 2.50)] // 10c vs 15c
    [InlineData(1.00, 0.99)] // 1c vs 25c
    [InlineData(9.99, 9.99)] // ya es terminación
    [InlineData(0.24, 0.25)] // sin escalón inferior positivo
    public void Round_UsdPsychological_ReturnsNearestTermination(decimal amount, decimal expected)
    {
        var result = Policy.Round(amount, RoundingRuleType.UsdPsychological, 0m, null, false);

        Assert.Equal(expected, result.RoundedValue);
        Assert.Equal(expected - amount, result.RoundingDelta);
    }

    [Theory]
    [InlineData(1.375, 1.25)] // empate 12.5c/12.5c -> escalón inferior
    [InlineData(1.85, 1.75)]  // empate 10c/10c
    [InlineData(0.85, 0.75)]  // empate 10c/10c en < $1
    public void Round_UsdPsychological_OnTie_UsesLowerStep(decimal amount, decimal expected)
    {
        var result = Policy.Round(amount, RoundingRuleType.UsdPsychological, 0m, null, false);

        Assert.Equal(expected, result.RoundedValue);
    }

    // ---------------------------------------------------------------------------------------------
    // VesCashFriendly*: escalones de 1 / 5 / 10 Bs.S, nearest bidireccional (tasas altas incluidas).
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 682.19, 682)]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 682.50, 682)] // empate -> inferior
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 682.19, 680)]   // 2.19 vs 2.81
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 682.60, 685)]   // 2.60 vs 2.40
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 682.50, 680)]   // empate -> inferior
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 682.19, 680)]  // 2.19 vs 7.81
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 8422.10, 8420)] // valores Bs.S altos
    public void Round_VesCashFriendly_ReturnsNearestStep(RoundingRuleType rule, decimal amount, decimal expected)
    {
        var result = Policy.Round(amount, rule, 0m, null, false);

        Assert.Equal(expected, result.RoundedValue);
        Assert.Equal(expected - amount, result.RoundingDelta);
    }

    [Fact]
    public void Round_VesCashFriendly_WhenLowerStepIsNotPositive_UsesUpperStep()
    {
        // 2 Bs.S con paso 5: el escalón inferior (0) no es positivo -> escala hacia arriba.
        var result = Policy.Round(2m, RoundingRuleType.VesCashFriendlyStep5, 0m, null, false);

        Assert.Equal(5m, result.RoundedValue);
        Assert.Equal(3m, result.RoundingDelta);
    }

    // ---------------------------------------------------------------------------------------------
    // S2: piso de margen evaluado sobre el precio FINAL.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Round_MarginFloor_RejectsLowerStepAndUsesNextStepUp()
    {
        // 1.37 -> 1.25 (25% con costo 1.00) viola el piso 30% -> escalón superior 1.50 (50%).
        var result = Policy.Round(1.37m, RoundingRuleType.UsdPsychological, 1.00m, 30m, true);

        Assert.Equal(1.50m, result.RoundedValue);
        Assert.Equal(0.13m, result.RoundingDelta);
        Assert.Equal(50.00m, result.EffectiveMarginPct);
    }

    [Fact]
    public void Round_MarginFloor_RejectsTieLowerStepAndUsesNextStepUp()
    {
        // Empate 1.375 -> 1.25 (25%) rechazado por el piso 30% -> 1.50.
        var result = Policy.Round(1.375m, RoundingRuleType.UsdPsychological, 1.00m, 30m, true);

        Assert.Equal(1.50m, result.RoundedValue);
    }

    [Fact]
    public void Round_MarginFloorDisabled_KeepsNearestStep()
    {
        Assert.Equal(1.25m, Policy.Round(1.37m, RoundingRuleType.UsdPsychological, 1.00m, 30m, false).RoundedValue);
        Assert.Equal(1.25m, Policy.Round(1.37m, RoundingRuleType.UsdPsychological, 1.00m, null, true).RoundedValue);
        Assert.Equal(1.25m, Policy.Round(1.37m, RoundingRuleType.UsdPsychological, 1.00m, 0m, true).RoundedValue);
    }

    [Fact]
    public void Round_MarginFloorWithNonPositiveCost_IsInert()
    {
        var result = Policy.Round(1.37m, RoundingRuleType.UsdPsychological, 0m, 30m, true);

        Assert.Equal(1.25m, result.RoundedValue);
        Assert.Null(result.EffectiveMarginPct);
    }

    [Fact]
    public void Round_MarginFloor_OnVesSteps_UsesNextBsSStep()
    {
        // 682.19 -> 680 (25/655 = 3.82%) viola el piso 3.85% -> 685 (4.58%).
        var result = Policy.Round(682.19m, RoundingRuleType.VesCashFriendlyStep5, 655.00m, 3.85m, true);

        Assert.Equal(685.00m, result.RoundedValue);
        Assert.Equal(4.58m, result.EffectiveMarginPct);
    }

    // ---------------------------------------------------------------------------------------------
    // 8.183 (R1): proyección en caliente del Bs.S de efectivo (JAMÁS persistida).
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 1095)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 1095)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 1090)]
    public void ProjectCashFriendlyBsS_ConvertsUsdWithDailyRateAndRoundsWithChannelRule(RoundingRuleType rule, decimal expected)
    {
        // 1.30 × 842.21 = 1094.873 → escalón más cercano en Bs.S (empate hacia el inferior).
        var projection = Policy.ProjectCashFriendlyBsS(1.30m, 842.21m, rule, 1.00m, null, false);

        Assert.NotNull(projection);
        Assert.Equal(1094.873m, projection!.Value.MathematicalValue);
        Assert.Equal(expected, projection.Value.RoundedValue);
    }

    [Theory]
    [InlineData(RoundingRuleType.ExactMath)]
    [InlineData(RoundingRuleType.UsdPsychological)]
    public void ProjectCashFriendlyBsS_WithNonCashRule_ReturnsNull(RoundingRuleType rule)
    {
        Assert.Null(Policy.ProjectCashFriendlyBsS(1.30m, 842.21m, rule, 1m, null, false));
    }

    [Fact]
    public void ProjectCashFriendlyBsS_WithoutRateOrConvertibleUsd_ReturnsNull()
    {
        Assert.Null(Policy.ProjectCashFriendlyBsS(0m, 842.21m, RoundingRuleType.VesCashFriendlyStep5, 1m, null, false));
        Assert.Null(Policy.ProjectCashFriendlyBsS(1.30m, 0m, RoundingRuleType.VesCashFriendlyStep5, 1m, null, false));
    }

    [Fact]
    public void ProjectCashFriendlyBsS_WithMarginFloor_ProjectsNextStepUp()
    {
        // Costo 1.00 → 842.21 Bs.S: el paso 10 de 1094.873 es 1090 (margen 29.42%) y viola el piso
        // 30% → proyección 1100 (30.61%).
        var projection = Policy.ProjectCashFriendlyBsS(1.30m, 842.21m, RoundingRuleType.VesCashFriendlyStep10, 1.00m, 30m, true);

        Assert.NotNull(projection);
        Assert.Equal(1100m, projection!.Value.RoundedValue);
        Assert.Equal(30.61m, projection.Value.EffectiveMarginPct);
    }

    [Fact]
    public void ProjectCashFriendlyBsS_UnknownRule_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Policy.ProjectCashFriendlyBsS(1.30m, 842.21m, (RoundingRuleType)99, 1m, null, false));
    }

    // ---------------------------------------------------------------------------------------------
    // Importes no positivos y reglas desconocidas.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-3.5)]
    public void Round_NonPositiveAmount_IsPassthrough(decimal amount)
    {
        var result = Policy.Round(amount, RoundingRuleType.UsdPsychological, 1.00m, 30m, true);

        Assert.Equal(amount, result.RoundedValue);
        Assert.Equal(0m, result.RoundingDelta);
    }

    [Fact]
    public void Round_UnknownRule_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Policy.Round(1.37m, (RoundingRuleType)99, 1m, null, false));
    }

    // ---------------------------------------------------------------------------------------------
    // S3: escalón inmediato inferior (insumo de la salvaguarda Detal/Mayor).
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RoundingRuleType.UsdPsychological, 1.50, 1.25)]
    [InlineData(RoundingRuleType.UsdPsychological, 1.25, 0.99)]
    [InlineData(RoundingRuleType.UsdPsychological, 1.00, 0.99)]
    [InlineData(RoundingRuleType.UsdPsychological, 0.99, 0.95)]
    [InlineData(RoundingRuleType.ExactMath, 8.00, 7.99)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 8.00, 7.99)] // regla VES sin escalera USD: centavo
    public void FindLowerStep_ReturnsImmediateLowerStep(RoundingRuleType rule, decimal amount, decimal expected)
    {
        Assert.Equal(expected, Policy.FindLowerStep(amount, rule));
    }

    [Fact]
    public void FindLowerStep_BelowSmallestTermination_ReturnsNull()
    {
        Assert.Null(Policy.FindLowerStep(0.25m, RoundingRuleType.UsdPsychological));
    }

    [Fact]
    public void FindLowerStep_UnknownRule_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Policy.FindLowerStep(1.50m, (RoundingRuleType)99));
    }

    // ---------------------------------------------------------------------------------------------
    // 8.185-T1 (S3): decisión A — proyección geométrica SIN costo (toggle ON ⇒ nunca hacia abajo).
    // Mismos guards que la proyección 8.183: sin regla de efectivo, sin USD positivo o sin tasa → null.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RoundingRuleType.ExactMath)]
    [InlineData(RoundingRuleType.UsdPsychological)]
    public void ProjectCashFriendlyBsS_NeverRoundDown_WithNonCashRule_ReturnsNull(RoundingRuleType rule)
    {
        Assert.Null(PricingRoundingPolicy.ProjectCashFriendlyBsS(1.30m, 842.21m, rule, neverRoundDown: true));
    }

    [Fact]
    public void ProjectCashFriendlyBsS_NeverRoundDown_WithoutRateOrConvertibleUsd_ReturnsNull()
    {
        Assert.Null(PricingRoundingPolicy.ProjectCashFriendlyBsS(0m, 842.21m, RoundingRuleType.VesCashFriendlyStep5, neverRoundDown: true));
        Assert.Null(PricingRoundingPolicy.ProjectCashFriendlyBsS(-1m, 842.21m, RoundingRuleType.VesCashFriendlyStep5, neverRoundDown: true));
        Assert.Null(PricingRoundingPolicy.ProjectCashFriendlyBsS(1.30m, 0m, RoundingRuleType.VesCashFriendlyStep5, neverRoundDown: true));
        Assert.Null(PricingRoundingPolicy.ProjectCashFriendlyBsS(1.30m, -5m, RoundingRuleType.VesCashFriendlyStep5, neverRoundDown: true));
    }

    [Theory]
    // Tasa 1: el importe matemático coincide con el Bs.S indicado (foco en la escalera, no en la conversión).
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 682.19, 683)] // nearest 682 queda por debajo ⇒ sube
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 682.50, 683)] // empate del nearest (682) ⇒ sube
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 682.19, 685)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 682.50, 685)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 682.19, 690)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 682.50, 690)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 2.00, 5)] // sin escalón inferior positivo
    public void ProjectCashFriendlyBsS_NeverRoundDown_UsesSmallestStepAtOrAboveTheMathValue(RoundingRuleType rule, decimal amountBsS, decimal expected)
    {
        var projection = PricingRoundingPolicy.ProjectCashFriendlyBsS(amountBsS, 1m, rule, neverRoundDown: true);

        Assert.NotNull(projection);
        Assert.Equal(amountBsS, projection!.Value.MathematicalValue);
        Assert.Equal(expected, projection.Value.RoundedValue);
        Assert.Equal(expected - amountBsS, projection.Value.RoundingDelta);
    }

    [Theory]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 682, 682)] // coincidencia exacta con el escalón: no sube
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 685, 685)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 680, 680)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 682.60, 685)] // el nearest ya es superior: queda
    public void ProjectCashFriendlyBsS_NeverRoundDown_OnExactOrUpperStep_KeepsTheStep(RoundingRuleType rule, decimal amountBsS, decimal expected)
    {
        var projection = PricingRoundingPolicy.ProjectCashFriendlyBsS(amountBsS, 1m, rule, neverRoundDown: true);

        Assert.NotNull(projection);
        Assert.Equal(expected, projection!.Value.RoundedValue);
    }

    [Theory]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 682.19, 682)]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 682.50, 682)] // empate ⇒ inferior
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 682.19, 680)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 682.50, 680)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 682.19, 680)]
    public void ProjectCashFriendlyBsS_WithoutNeverRoundDown_KeepsTheNearestStep(RoundingRuleType rule, decimal amountBsS, decimal expected)
    {
        var projection = PricingRoundingPolicy.ProjectCashFriendlyBsS(amountBsS, 1m, rule, neverRoundDown: false);

        Assert.NotNull(projection);
        Assert.Equal(expected, projection!.Value.RoundedValue);
        Assert.Equal(expected - amountBsS, projection.Value.RoundingDelta);
    }

    [Fact]
    public void ProjectCashFriendlyBsS_NeverRoundDown_UnknownRule_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PricingRoundingPolicy.ProjectCashFriendlyBsS(1.30m, 842.21m, (RoundingRuleType)99, neverRoundDown: true));
    }

    [Fact]
    public void ProjectCashFriendlyBsS_NeverRoundDown_MatchesTheMarginFloorEngineForAmountsAboveCost()
    {
        // S3: para amount > cost > 0 el piso geométrico equivale a enforzar como piso el margen
        // vigente del valor matemático con el motor costo-aware. Divergencia deliberada única:
        // productos bajo costo (margen ≤ 0), donde el motor actual no enforza y 8.185 sí sube.
        var rules = new[]
        {
            RoundingRuleType.VesCashFriendlyInteger,
            RoundingRuleType.VesCashFriendlyStep5,
            RoundingRuleType.VesCashFriendlyStep10
        };
        var rates = new[] { 36.50m, 60.75m, 842.21m };
        var prices = new[] { 0.75m, 1.30m, 2.99m, 8.55m };
        var costFactors = new[] { 0.5m, 0.6m };

        foreach (var rule in rules)
        {
            foreach (var rate in rates)
            {
                foreach (var price in prices)
                {
                    foreach (var factor in costFactors)
                    {
                        decimal amount = price * rate;
                        decimal costBsS = price * factor * rate;
                        decimal floorPct = PricingCalculator.RoundToDigital((amount - costBsS) / costBsS * 100m);

                        var expected = PricingRoundingPolicy.Round(amount, rule, costBsS, floorPct, enforceFloor: true);
                        var actual = PricingRoundingPolicy.ProjectCashFriendlyBsS(price, rate, rule, neverRoundDown: true);

                        Assert.NotNull(actual);
                        Assert.Equal(expected.MathematicalValue, actual!.Value.MathematicalValue);
                        Assert.Equal(expected.RoundedValue, actual.Value.RoundedValue);
                        Assert.True(
                            actual.Value.RoundedValue >= amount,
                            $"{rule} {price}×{rate}: el efectivo nunca queda por debajo del matemático.");
                    }
                }
            }
        }
    }
}
