using System;
using Core.DTOs;
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
}
