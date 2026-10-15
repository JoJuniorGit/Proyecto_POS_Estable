using System;
using Core.DTOs;
using Core.Helpers;
using Inventory.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.184-T1 (S3): el algoritmo puro de redondeo se relocalizó a Core.Helpers.PricingRoundingPolicy y
// el servicio de Inventory.Module solo delega. Este par garantiza paridad de resultados, proyección,
// escalón inferior y excepciones entre ambas superficies, y ancla los valores conocidos del suite
// 8.182 para que la paridad no pase "en falso" si el helper se degrada.
public class PricingRoundingPolicyDelegationTests
{
    private static readonly PricingRoundingPolicyService Service = new();

    // ---------------------------------------------------------------------------------------------
    // Round: paridad servicio vs helper sobre la escalera psico, VES y el piso de margen.
    // ---------------------------------------------------------------------------------------------

    // Nota xunit: InlineData numérico no convierte a decimal? en este runner; los casos con piso
    // viajan por parámetro decimal y los casos "sin piso" fijan null en el call site.
    [Theory]
    [InlineData(1.37, RoundingRuleType.UsdPsychological, 1.00, 30, true)]   // piso rechaza .25 -> .50
    [InlineData(12.46, RoundingRuleType.UsdPsychological, 10.00, 20, true)] // .46 -> .50
    [InlineData(682.19, RoundingRuleType.VesCashFriendlyStep5, 655.00, 3.85, true)]
    public void Round_ServiceDelegatesToHelper_WithMarginFloor(
        decimal amount, RoundingRuleType rule, decimal cost, decimal marginFloorPct, bool enforceFloor)
    {
        AssertRoundParity(amount, rule, cost, marginFloorPct, enforceFloor);
    }

    [Theory]
    [InlineData(9.94, RoundingRuleType.UsdPsychological, 8.00, false)]      // .94 -> .95
    [InlineData(7.01, RoundingRuleType.UsdPsychological, 5.00, false)]      // .01 -> .99 inferior
    [InlineData(8422.10, RoundingRuleType.VesCashFriendlyStep10, 7000.00, false)]
    public void Round_ServiceDelegatesToHelper_WithoutMarginFloor(
        decimal amount, RoundingRuleType rule, decimal cost, bool enforceFloor)
    {
        AssertRoundParity(amount, rule, cost, marginFloorPct: null, enforceFloor);
    }

    private static void AssertRoundParity(decimal amount, RoundingRuleType rule, decimal cost, decimal? marginFloorPct, bool enforceFloor)
    {
        var viaHelper = PricingRoundingPolicy.Round(amount, rule, cost, marginFloorPct, enforceFloor);
        var viaService = Service.Round(amount, rule, cost, marginFloorPct, enforceFloor);

        Assert.Equal(viaHelper.MathematicalValue, viaService.MathematicalValue);
        Assert.Equal(viaHelper.RoundedValue, viaService.RoundedValue);
        Assert.Equal(viaHelper.RoundingDelta, viaService.RoundingDelta);
        Assert.Equal(viaHelper.EffectiveMarginPct, viaService.EffectiveMarginPct);
    }

    [Fact]
    public void Round_DelegatedResults_AnchorKnownValues()
    {
        // Anclas concretas del suite 8.182: la paridad anterior no debe pasar "en falso".
        Assert.Equal(1.50m, Service.Round(1.37m, RoundingRuleType.UsdPsychological, 1.00m, 30m, true).RoundedValue);
        Assert.Equal(1.25m, Service.Round(1.37m, RoundingRuleType.UsdPsychological, 1.00m, 30m, false).RoundedValue);
        Assert.Equal(685m, Service.Round(682.19m, RoundingRuleType.VesCashFriendlyStep5, 655.00m, 3.85m, true).RoundedValue);
        Assert.Equal(8420m, Service.Round(8422.10m, RoundingRuleType.VesCashFriendlyStep10, 7000.00m, null, false).RoundedValue);
    }

    // ---------------------------------------------------------------------------------------------
    // FindLowerStep: salvaguarda Detal/Mayor sobre ambas superficies.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RoundingRuleType.UsdPsychological, 1.50, 1.25)]
    [InlineData(RoundingRuleType.UsdPsychological, 0.99, 0.95)]
    [InlineData(RoundingRuleType.ExactMath, 8.00, 7.99)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 8.00, 7.99)] // regla VES sin escalera USD: centavo
    public void FindLowerStep_ServiceDelegatesToHelper(RoundingRuleType rule, decimal amount, decimal expected)
    {
        Assert.Equal(expected, PricingRoundingPolicy.FindLowerStep(amount, rule));
        Assert.Equal(expected, Service.FindLowerStep(amount, rule));
    }

    [Fact]
    public void FindLowerStep_BelowSmallestTermination_BothSurfacesReturnNull()
    {
        Assert.Null(PricingRoundingPolicy.FindLowerStep(0.25m, RoundingRuleType.UsdPsychological));
        Assert.Null(Service.FindLowerStep(0.25m, RoundingRuleType.UsdPsychological));
    }

    // ---------------------------------------------------------------------------------------------
    // ProjectCashFriendlyBsS: proyección en caliente (JAMÁS persistida) desde ambas superficies.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 1095)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 1095)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 1090)]
    public void ProjectCashFriendlyBsS_ServiceDelegatesToHelper_WithIdenticalResult(RoundingRuleType rule, decimal expected)
    {
        // 1.30 × 842.21 = 1094.873 → escalón más cercano en Bs.S (empate hacia el inferior).
        var viaHelper = PricingRoundingPolicy.ProjectCashFriendlyBsS(1.30m, 842.21m, rule, 1.00m, null, false);
        var viaService = Service.ProjectCashFriendlyBsS(1.30m, 842.21m, rule, 1.00m, null, false);

        Assert.NotNull(viaHelper);
        Assert.NotNull(viaService);
        Assert.Equal(viaHelper!.MathematicalValue, viaService!.MathematicalValue);
        Assert.Equal(viaHelper.RoundedValue, viaService.RoundedValue);
        Assert.Equal(viaHelper.RoundingDelta, viaService.RoundingDelta);
        Assert.Equal(viaHelper.EffectiveMarginPct, viaService.EffectiveMarginPct);
        Assert.Equal(expected, viaService.RoundedValue);
    }

    [Fact]
    public void ProjectCashFriendlyBsS_WithMarginFloor_ServiceDelegatesToHelper()
    {
        // Paso 10 de 1094.873 → 1090 (margen 29.42%) viola el piso 30% → 1100 (30.61%) en ambas.
        var viaHelper = PricingRoundingPolicy.ProjectCashFriendlyBsS(1.30m, 842.21m, RoundingRuleType.VesCashFriendlyStep10, 1.00m, 30m, true);
        var viaService = Service.ProjectCashFriendlyBsS(1.30m, 842.21m, RoundingRuleType.VesCashFriendlyStep10, 1.00m, 30m, true);

        Assert.NotNull(viaHelper);
        Assert.NotNull(viaService);
        Assert.Equal(viaHelper!.RoundedValue, viaService!.RoundedValue);
        Assert.Equal(1100m, viaService.RoundedValue);
    }

    [Theory]
    [InlineData(RoundingRuleType.ExactMath)]
    [InlineData(RoundingRuleType.UsdPsychological)]
    public void ProjectCashFriendlyBsS_WithNonCashRule_BothSurfacesReturnNull(RoundingRuleType rule)
    {
        Assert.Null(PricingRoundingPolicy.ProjectCashFriendlyBsS(1.30m, 842.21m, rule, 1m, null, false));
        Assert.Null(Service.ProjectCashFriendlyBsS(1.30m, 842.21m, rule, 1m, null, false));
    }

    // ---------------------------------------------------------------------------------------------
    // Reglas desconocidas: misma excepción en ambas superficies.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void UnknownRule_BothSurfacesThrowArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PricingRoundingPolicy.Round(1.37m, (RoundingRuleType)99, 1m, null, false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Service.Round(1.37m, (RoundingRuleType)99, 1m, null, false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PricingRoundingPolicy.FindLowerStep(1.50m, (RoundingRuleType)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Service.FindLowerStep(1.50m, (RoundingRuleType)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PricingRoundingPolicy.ProjectCashFriendlyBsS(1.30m, 842.21m, (RoundingRuleType)99, 1m, null, false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Service.ProjectCashFriendlyBsS(1.30m, 842.21m, (RoundingRuleType)99, 1m, null, false));
    }
}
