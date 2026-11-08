using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Desktop.Client.Services;
using Xunit;
using static CommandCenter.Tests.Unit.ClientHttpContractGuard;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Client seam for the cash advance commission settings. The WPF Settings screen reads both
/// channels from the same endpoint and persists them together so the coordinator never sees a
/// half-configured pair.
/// </summary>
public sealed class SettingsCashAdvanceCommissionClientTests
{
    [Fact]
    public async Task GetCashAdvanceCommissionAsync_GetsSettingsRoute()
    {
        var requests = await CaptureAsync<SettingsService>(
            client => new SettingsService(client),
            service => service.GetCashAdvanceCommissionAsync(),
            payload: "{\"transferCommissionPct\":7,\"cashCommissionPct\":5}");

        var request = Assert.Single(requests);
        AssertRequest(
            request,
            HttpMethod.Get,
            "/api/settings/cash-advance-commission",
            typeof(SettingsController),
            nameof(SettingsController.GetCashAdvanceCommissionAsync));
    }

    [Fact]
    public async Task SetCashAdvanceCommissionAsync_PutsBothChannelsTogether()
    {
        var requests = await CaptureAsync<SettingsService>(
            client => new SettingsService(client),
            service => service.SetCashAdvanceCommissionAsync(new CashAdvanceCommissionSettingsDto
            {
                TransferCommissionPct = 7m,
                CashCommissionPct = 5m
            }));

        var request = Assert.Single(requests);
        AssertRequest(
            request,
            HttpMethod.Put,
            "/api/settings/cash-advance-commission",
            typeof(SettingsController),
            nameof(SettingsController.SetCashAdvanceCommissionAsync));

        Assert.False(string.IsNullOrWhiteSpace(request.Body), "El PUT de comisiones debe llevar body.");
        using var document = JsonDocument.Parse(request.Body!);
        Assert.Equal(7m, document.RootElement.GetProperty("transferCommissionPct").GetDecimal());
        Assert.Equal(5m, document.RootElement.GetProperty("cashCommissionPct").GetDecimal());
    }
}
