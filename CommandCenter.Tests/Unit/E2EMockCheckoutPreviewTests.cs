using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.156 (W1 de la verificación): el mock del modo --e2e debe servir /checkout-preview con el
/// contrato real del servidor; sin él, el gate del checkout WPF (preview fresco requerido) queda
/// cerrado para siempre en modo mock y "COBRAR Y FINALIZAR" nunca se habilita.
/// </summary>
public class E2EMockCheckoutPreviewTests
{
    [Fact]
    public async Task CheckoutPreview_ReturnsCanonicalShape_AndMarksCoveredPaymentsAsFullyPaid()
    {
        using var client = new HttpClient(new E2EMockHttpMessageHandler())
        {
            BaseAddress = new System.Uri("http://localhost/")
        };
        client.DefaultRequestHeaders.Add("X-Client-Version", "1.0.0");

        using var startResponse = await client.PostAsync("api/sales/start", null);
        startResponse.EnsureSuccessStatusCode();

        using var response = await client.PostAsJsonAsync("api/sales/1/checkout-preview", new
        {
            exchangeRate = 50m,
            payments = new[]
            {
                new
                {
                    paymentMethodId = 1,
                    amount = 1.00m,
                    amountBsS = 50m,
                    amountLocal = 50m,
                    referenceNumber = (string?)null
                }
            }
        });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.True(root.TryGetProperty("isFullyPaid", out var fullyPaid));
        Assert.True(fullyPaid.GetBoolean());
        Assert.True(root.TryGetProperty("roundingAdjustment", out _));
        Assert.True(root.TryGetProperty("remainingBalanceUSD", out var remaining));
        Assert.True(remaining.GetDecimal() <= 0.05m);
        Assert.True(root.TryGetProperty("totalPaidBsS", out var paidBsS));
        Assert.Equal(50m, paidBsS.GetDecimal());
    }
}
