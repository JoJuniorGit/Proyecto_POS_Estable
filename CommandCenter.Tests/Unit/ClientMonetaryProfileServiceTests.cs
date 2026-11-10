using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T4c1 (S9): contrato HTTP read-only del perfil monetario para el cliente WPF. La lectura es
// best-effort: cualquier fallo (401, 5xx, JSON inválido, cancelación) devuelve null para que el
// arranque conserve los defaults Dual USD/VES y no bloquee MainWindow.Show().
public class ClientMonetaryProfileServiceTests
{
    [Fact]
    public async Task GetAsync_SendsGetToClientMonetaryProfileRoute()
    {
        var handler = new StubHttpMessageHandler(JsonResponse(
            """{"mode":1,"primaryCurrencyCode":"USD","primaryCurrencySymbol":"$","secondaryCurrencyCode":"VES","secondaryCurrencySymbol":"Bs.S","isConfigured":true}"""));
        var service = CreateService(handler);

        await service.GetAsync();

        Assert.Equal("GET", handler.RequestMethods[0]);
        Assert.Equal("/api/client/monetary-profile", handler.RequestPaths[0]);
    }

    [Fact]
    public async Task GetAsync_DeserializesDualProfileWithNumericMode()
    {
        var handler = new StubHttpMessageHandler(JsonResponse(
            """{"mode":1,"primaryCurrencyCode":"USD","primaryCurrencySymbol":"$","secondaryCurrencyCode":"COP","secondaryCurrencySymbol":"COL$","isConfigured":true}"""));
        var service = CreateService(handler);

        var profile = await service.GetAsync();

        Assert.NotNull(profile);
        Assert.Equal(MonetaryMode.Dual, profile!.Mode);
        Assert.Equal("USD", profile.PrimaryCurrencyCode);
        Assert.Equal("$", profile.PrimaryCurrencySymbol);
        Assert.Equal("COP", profile.SecondaryCurrencyCode);
        Assert.Equal("COL$", profile.SecondaryCurrencySymbol);
        Assert.True(profile.IsConfigured);
    }

    [Fact]
    public async Task GetAsync_DeserializesSingleProfileWithNullSecondary()
    {
        var handler = new StubHttpMessageHandler(JsonResponse(
            """{"mode":0,"primaryCurrencyCode":"EUR","primaryCurrencySymbol":"€","secondaryCurrencyCode":null,"secondaryCurrencySymbol":null,"isConfigured":true}"""));
        var service = CreateService(handler);

        var profile = await service.GetAsync();

        Assert.NotNull(profile);
        Assert.Equal(MonetaryMode.Single, profile!.Mode);
        Assert.Equal("EUR", profile.PrimaryCurrencyCode);
        Assert.Equal("€", profile.PrimaryCurrencySymbol);
        Assert.Null(profile.SecondaryCurrencyCode);
        Assert.Null(profile.SecondaryCurrencySymbol);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetAsync_ReturnsNullOnHttpFailure(HttpStatusCode status)
    {
        var service = CreateService(new StubHttpMessageHandler(new HttpResponseMessage(status)));

        Assert.Null(await service.GetAsync());
    }

    [Fact]
    public async Task GetAsync_ReturnsNullOnMalformedJson()
    {
        var service = CreateService(new StubHttpMessageHandler(JsonResponse("{not-json")));

        Assert.Null(await service.GetAsync());
    }

    [Fact]
    public async Task GetAsync_ReturnsNullOnEmptyBody()
    {
        var service = CreateService(new StubHttpMessageHandler(JsonResponse(string.Empty)));

        Assert.Null(await service.GetAsync());
    }

    [Fact]
    public async Task GetAsync_ReturnsNullWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = CreateService(new StubHttpMessageHandler(JsonResponse("{}")));

        Assert.Null(await service.GetAsync(cts.Token));
    }

    private static ClientMonetaryProfileService CreateService(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestPaths { get; } = new();
        public List<string> RequestMethods { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            RequestMethods.Add(request.Method.Method);

            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
