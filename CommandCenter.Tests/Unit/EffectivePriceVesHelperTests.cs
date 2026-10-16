using Core.DTOs;
using Core.Helpers;
using Desktop.Client.Helpers;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.185-T4 (S1/S7): contrato del helper ÚNICO del cliente para el precio efectivo Bs.S.
/// Identidad matemática con el motor compartido (S3 — misma función, mismos inputs), techo
/// canónico 8.104 para regla null/ExactMath/sin tasa (S7) y decisión A (toggle ON ⇒
/// nunca-hacia-abajo; OFF ⇒ escalón más cercano).
/// </summary>
public class EffectivePriceVesHelperTests
{
    [Theory]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 11.89, 36.5, true)]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 11.89, 36.5, false)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 12, 36.5, true)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 12, 36.5, false)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 11.89, 36.5, true)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 11.89, 36.5, false)]
    public void EffectiveBsS_WithCashRule_IsIdenticalToSharedEngine(RoundingRuleType rule, double usd, double rate, bool neverRoundDown)
    {
        decimal usdAmount = (decimal)usd;
        decimal exchangeRate = (decimal)rate;

        var engine = PricingRoundingPolicy.ProjectCashFriendlyBsS(usdAmount, exchangeRate, rule, neverRoundDown);

        Assert.NotNull(engine);
        Assert.Equal(engine!.Value.RoundedValue, EffectivePriceVesHelper.EffectiveBsS(usdAmount, exchangeRate, rule, neverRoundDown));
    }

    [Fact]
    public void EffectiveBsS_ToggleOn_PushesUpWhenNearestStepFallsBelowMath()
    {
        // 11.89 × 36.50 = 433.985 → múltiplo de 10 más cercano 430 (por debajo) → escalón inmediato superior.
        Assert.Equal(440m, EffectivePriceVesHelper.EffectiveBsS(11.89m, 36.50m, RoundingRuleType.VesCashFriendlyStep10, neverRoundDown: true));
    }

    [Fact]
    public void EffectiveBsS_ToggleOff_KeepsNearestStep()
    {
        Assert.Equal(430m, EffectivePriceVesHelper.EffectiveBsS(11.89m, 36.50m, RoundingRuleType.VesCashFriendlyStep10, neverRoundDown: false));
    }

    [Fact]
    public void EffectiveBsS_ExactMath_ReturnsCanonicalCeiling()
    {
        Assert.Equal(
            PricingCalculator.ToBsSCeiling(12.345m, 36.50m),
            EffectivePriceVesHelper.EffectiveBsS(12.345m, 36.50m, RoundingRuleType.ExactMath, neverRoundDown: true));
    }

    [Fact]
    public void EffectiveBsS_NullRule_ReturnsCanonicalCeiling()
    {
        Assert.Equal(
            PricingCalculator.ToBsSCeiling(12m, 36.50m),
            EffectivePriceVesHelper.EffectiveBsS(12m, 36.50m, cashRule: null, neverRoundDown: true));
    }

    [Fact]
    public void EffectiveBsS_NonPositiveRate_ReturnsZero()
    {
        Assert.Equal(0m, EffectivePriceVesHelper.EffectiveBsS(12m, 0m, RoundingRuleType.VesCashFriendlyStep5, neverRoundDown: true));
        Assert.Equal(0m, EffectivePriceVesHelper.EffectiveBsS(12m, -1m, RoundingRuleType.VesCashFriendlyStep5, neverRoundDown: true));
    }

    [Fact]
    public void EffectiveBsS_NonPositiveUsd_ReturnsZeroWithoutDelegating()
    {
        Assert.Equal(0m, EffectivePriceVesHelper.EffectiveBsS(0m, 36.50m, RoundingRuleType.VesCashFriendlyStep5, neverRoundDown: true));
        Assert.Equal(0m, EffectivePriceVesHelper.EffectiveBsS(0m, 36.50m, cashRule: null, neverRoundDown: true));
    }
}
