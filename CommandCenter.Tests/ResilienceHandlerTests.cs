using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests;

public class ResilienceHandlerTests
{
    private sealed class MockInnerHandler : HttpMessageHandler
    {
        public int InvocationCount { get; private set; }
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            InvocationCount++;
            var response = new HttpResponseMessage(ResponseStatusCode)
            {
                Content = new StringContent(ResponseBody),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ResilienceHandler_WhenFatalErrorActive_Returns503Immediately()
    {
        var clientState = new ClientStateService();
        clientState.TryActivateFatalError();

        var innerHandler = new MockInnerHandler { ResponseStatusCode = HttpStatusCode.OK };
        var healthPollingMock = new HealthPollingService(new HttpClient(new MockInnerHandler()) { BaseAddress = new Uri("http://localhost:5000/") });
        var resilienceHandler = new ResilienceHandler(healthPollingMock, clientState)
        {
            InnerHandler = innerHandler
        };

        using var invoker = new HttpMessageInvoker(resilienceHandler);
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/api/products");

        var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, innerHandler.InvocationCount); // Should NOT call backend (fail-fast)
    }

    [Fact]
    public async Task ResilienceHandler_On4xx_DoesNotActivateCircuitBreaker()
    {
        var clientState = new ClientStateService();
        var innerHandler = new MockInnerHandler { ResponseStatusCode = HttpStatusCode.BadRequest, ResponseBody = "{\"message\":\"Invalid model\"}" };
        var healthPollingMock = new HealthPollingService(new HttpClient(new MockInnerHandler()) { BaseAddress = new Uri("http://localhost:5000/") });
        var resilienceHandler = new ResilienceHandler(healthPollingMock, clientState)
        {
            InnerHandler = innerHandler
        };

        using var invoker = new HttpMessageInvoker(resilienceHandler);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:5000/api/sales/start");

        var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, innerHandler.InvocationCount); // 4xx must not retry
        Assert.False(clientState.IsFatalErrorActive); // Circuit breaker should remain closed (inactive)
        Assert.False(healthPollingMock.IsPollingActive);
    }

    [Fact]
    public async Task ResilienceHandler_On5xx_ActivatesCircuitBreakerWhenExhausted()
    {
        var clientState = new ClientStateService();
        var innerHandler = new MockInnerHandler { ResponseStatusCode = HttpStatusCode.InternalServerError, ResponseBody = "Internal Server Error" };
        var healthPollingMock = new HealthPollingService(new HttpClient(new MockInnerHandler { ResponseStatusCode = HttpStatusCode.ServiceUnavailable }) { BaseAddress = new Uri("http://localhost:5000/") });
        var resilienceHandler = new ResilienceHandler(healthPollingMock, clientState)
        {
            InnerHandler = innerHandler
        };

        using var invoker = new HttpMessageInvoker(resilienceHandler);
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/api/products");

        var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, innerHandler.InvocationCount); // Reattempted 3 times
        Assert.True(clientState.IsFatalErrorActive); // Circuit breaker is now active
        Assert.True(healthPollingMock.IsPollingActive); // Health polling triggered
    }

    [Fact]
    public async Task ResilienceHandler_AuthEndpoint_BypassesCircuitBreaker()
    {
        var clientState = new ClientStateService();
        clientState.TryActivateFatalError(); // System is in fatal error state

        var innerHandler = new MockInnerHandler { ResponseStatusCode = HttpStatusCode.OK, ResponseBody = "{\"token\":\"jwt-token\"}" };
        var healthPollingMock = new HealthPollingService(new HttpClient(new MockInnerHandler()) { BaseAddress = new Uri("http://localhost:5000/") });
        var resilienceHandler = new ResilienceHandler(healthPollingMock, clientState)
        {
            InnerHandler = innerHandler
        };

        using var invoker = new HttpMessageInvoker(resilienceHandler);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:5000/api/auth/login");

        var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, innerHandler.InvocationCount); // Must bypass fail-fast and invoke login endpoint
    }
}
