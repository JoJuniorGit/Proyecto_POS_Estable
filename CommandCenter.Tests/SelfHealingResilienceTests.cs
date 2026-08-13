using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.Logging;
using Desktop.Client.Services;
using Moq;
using Xunit;

namespace CommandCenter.Tests;

public class SelfHealingResilienceTests
{
    [Fact]
    public void ClientStateLogger_WritesFormattedLogsToDisk()
    {
        ClientStateLogger.LogRetry(2, 3, "/api/sales", "POST");
        ClientStateLogger.LogAuditSuppressedReplay("Cerrar Venta");

        Assert.True(File.Exists(ClientStateLogger.ResilienceLogPath));
        var content = File.ReadAllText(ClientStateLogger.ResilienceLogPath);
        Assert.Contains("[WARNING]", content);
        Assert.Contains("Reintentando petición POST /api/sales", content);
        Assert.Contains("[AUDIT]", content);
        Assert.Contains("Auto-replay suprimido", content);
    }

    [Fact]
    public async Task ResilienceHandler_FastFails_OnDbAuthFailed()
    {
        var mockHealthService = new Mock<IHealthPollingService>();
        var handler = new ResilienceHandler(mockHealthService.Object)
        {
            InnerHandler = new MockHttpMessageHandler((req, token) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"DB_AUTH_FAILED\",\"message\":\"Fallo de autenticación en PostgreSQL (28P01)\"}")
                };
                return Task.FromResult(response);
            })
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        await Assert.ThrowsAsync<FatalDbAuthenticationException>(() => client.GetAsync("api/sales"));

        // Must NOT start health polling on fatal auth fast-fail
        mockHealthService.Verify(h => h.StartPolling(), Times.Never);
    }

    [Fact]
    public async Task ResilienceHandler_TransitionsToHealthPolling_WhenRetriesExhaust()
    {
        var mockHealthService = new Mock<IHealthPollingService>();
        var handler = new ResilienceHandler(mockHealthService.Object)
        {
            InnerHandler = new MockHttpMessageHandler((req, token) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"DatabaseConnectionError\",\"message\":\"Connection refused\"}")
                };
                return Task.FromResult(response);
            })
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        var response = await client.GetAsync("api/sales");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // Verify that HealthPollingService.StartPolling() was called ONCE retries were exhausted
        mockHealthService.Verify(h => h.StartPolling(), Times.Once);
    }
}

public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;

    public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        _sendAsync = sendAsync;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _sendAsync(request, cancellationToken);
    }
}
