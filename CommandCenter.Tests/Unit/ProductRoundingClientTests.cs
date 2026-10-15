using Backend.API.Controllers;
using Core.DTOs;
using Desktop.Client.Services;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using static CommandCenter.Tests.Unit.ClientHttpContractGuard;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.184-T3 (S4/S5): costuras cliente de las reglas de redondeo por artículo. Cubre la obligación
/// cruzada de T2: cada guardado que parte de un ProductDto releído (edición, auto-save, variantes)
/// o del resultado del diálogo debe reenviar RoundingRuleUsd/RoundingRuleCashBs; si el mapeo los
/// dropea, cada edición limpia en silencio los overrides del artículo. Además ancla la lectura de
/// la política global (ruta contra la acción real del controlador + fallback silencioso).
/// </summary>
public class ProductRoundingClientTests
{
    [Fact]
    public void ToUpdateProductDto_FromProductDto_ForwardsArticleRoundingOverrides()
    {
        var source = new ProductDto
        {
            Id = 7,
            Name = "Harina P.A.N.",
            SKU = "H-1",
            RoundingRuleUsd = RoundingRuleType.UsdPsychological,
            RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep5,
        };

        var update = ProductClientMapping.ToUpdateProductDto(source);

        Assert.Equal(RoundingRuleType.UsdPsychological, update.RoundingRuleUsd);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, update.RoundingRuleCashBs);
    }

    [Fact]
    public void ToUpdateProductDto_FromProductDto_KeepsInheritWhenArticleHasNoOverride()
    {
        var update = ProductClientMapping.ToUpdateProductDto(new ProductDto { Id = 7, Name = "Harina P.A.N.", SKU = "H-1" });

        Assert.Null(update.RoundingRuleUsd);
        Assert.Null(update.RoundingRuleCashBs);
    }

    [Fact]
    public void ToUpdateProductDto_FromCreateProductDto_ForwardsDialogSelection()
    {
        var result = new CreateProductDto
        {
            Name = "Harina P.A.N.",
            SKU = "H-1",
            RoundingRuleUsd = RoundingRuleType.UsdPsychological,
            RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyInteger,
        };

        var update = ProductClientMapping.ToUpdateProductDto(result, 7);

        Assert.Equal(RoundingRuleType.UsdPsychological, update.RoundingRuleUsd);
        Assert.Equal(RoundingRuleType.VesCashFriendlyInteger, update.RoundingRuleCashBs);
    }

    [Fact]
    public void ToUpdateProductDto_FromCreateProductDto_KeepsInheritWhenDialogSelectedInherit()
    {
        var result = new CreateProductDto { Name = "Harina P.A.N.", SKU = "H-1" };

        var update = ProductClientMapping.ToUpdateProductDto(result, 7);

        Assert.Null(update.RoundingRuleUsd);
        Assert.Null(update.RoundingRuleCashBs);
    }

    [Fact]
    public void Merge_KeepsSavedRoundingOverridesOnLocalSnapshot()
    {
        var target = new ProductDto { Id = 7, Name = "Harina P.A.N.", SKU = "H-1" };
        var saved = new CreateProductDto
        {
            Name = "Harina P.A.N.",
            SKU = "H-1",
            RoundingRuleUsd = RoundingRuleType.UsdPsychological,
        };

        var merged = ProductClientMapping.Merge(target, saved);

        Assert.Equal(RoundingRuleType.UsdPsychological, merged.RoundingRuleUsd);
        Assert.Null(merged.RoundingRuleCashBs);
    }

    [Fact]
    public async Task Settings_GetPricingPoliciesAsync_GetsPricingPoliciesRoute()
    {
        var requests = await CaptureAsync<SettingsService>(c => new SettingsService(c), s => s.GetPricingPoliciesAsync());

        AssertRequest(
            Assert.Single(requests),
            HttpMethod.Get,
            "/api/settings/pricing-policies",
            typeof(SettingsController),
            nameof(SettingsController.GetPricingPoliciesAsync));
    }

    [Fact]
    public async Task Settings_GetPricingPoliciesAsync_ReadsServerValues()
    {
        var handler = new CapturingHandler("""{"usdRoundingRule":1,"cashBsRoundingRule":3,"enforceMinimumMarginFloor":false}""");
        using var client = CreateClient(handler);

        var policies = await new SettingsService(client).GetPricingPoliciesAsync();

        Assert.Equal(RoundingRuleType.UsdPsychological, policies.UsdRoundingRule);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, policies.CashBsRoundingRule);
        Assert.False(policies.EnforceMinimumMarginFloor);
    }

    [Fact]
    public async Task Settings_GetPricingPoliciesAsync_WhenServerFails_ReturnsSafeDefaults()
    {
        var handler = new CapturingHandler("{}", HttpStatusCode.InternalServerError);
        using var client = CreateClient(handler);

        var policies = await new SettingsService(client).GetPricingPoliciesAsync();

        Assert.Equal(RoundingRuleType.ExactMath, policies.UsdRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, policies.CashBsRoundingRule);
        Assert.True(policies.EnforceMinimumMarginFloor);
    }
}
