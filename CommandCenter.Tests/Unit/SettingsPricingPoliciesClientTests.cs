using Backend.API.Controllers;
using Core.DTOs;
using Desktop.Client.Services;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using static CommandCenter.Tests.Unit.ClientHttpContractGuard;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.184-T4 (S2): costura cliente del auto-guardado de las políticas globales de precios y
/// redondeo. El PUT viaja contra la acción real del controlador con los TRES campos juntos
/// (regla USD, regla de efectivo Bs.S y toggle de piso) y propaga el error del servidor para
/// que el ViewModel revierta la UI (molde allow-negative-stock).
/// </summary>
public class SettingsPricingPoliciesClientTests
{
    [Fact]
    public async Task SetPricingPoliciesAsync_PutsPricingPoliciesRouteWithAllThreeFields()
    {
        var requests = await CaptureAsync<SettingsService>(
            client => new SettingsService(client),
            service => service.SetPricingPoliciesAsync(new PricingPoliciesDto
            {
                UsdRoundingRule = RoundingRuleType.UsdPsychological,
                CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5,
                EnforceMinimumMarginFloor = false
            }));

        var request = Assert.Single(requests);
        AssertRequest(
            request,
            HttpMethod.Put,
            "/api/settings/pricing-policies",
            typeof(SettingsController),
            nameof(SettingsController.SetPricingPoliciesAsync));

        Assert.False(string.IsNullOrWhiteSpace(request.Body), "El PUT de políticas debe llevar body.");
        using var document = JsonDocument.Parse(request.Body!);
        var root = document.RootElement;
        Assert.Equal(
            RoundingRuleType.UsdPsychological,
            (RoundingRuleType)root.GetProperty("usdRoundingRule").GetInt32());
        Assert.Equal(
            RoundingRuleType.VesCashFriendlyStep5,
            (RoundingRuleType)root.GetProperty("cashBsRoundingRule").GetInt32());
        Assert.False(root.GetProperty("enforceMinimumMarginFloor").GetBoolean());
    }

    [Fact]
    public async Task SetPricingPoliciesAsync_WhenServerFails_PropagatesError()
    {
        var handler = new CapturingHandler("{}", HttpStatusCode.InternalServerError);
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new SettingsService(client).SetPricingPoliciesAsync(new PricingPoliciesDto()));
    }
}
