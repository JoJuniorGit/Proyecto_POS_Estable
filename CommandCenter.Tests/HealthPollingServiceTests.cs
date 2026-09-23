using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests;

public class HealthPollingServiceTests
{
    /// <summary>
    /// Handler que responde 500 (servidor "caído") y cuenta cada petición: el bucle de sondeo
    /// debe seguir iterando mientras esté activo y detenerse por completo tras StopPolling().
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    [Fact]
    public async Task StopPolling_CancelsThePollingLoop_NoFurtherRequests()
    {
        var handler = new CountingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var pollInterval = TimeSpan.FromMilliseconds(50);
        using var service = new HealthPollingService(client, pollInterval: pollInterval);

        service.StartPolling();
        Assert.True(service.IsPollingActive);

        // Espera (con límite) a que el bucle itere al menos dos veces: prueba que está vivo y avanzando.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref handler.RequestCount) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(handler.RequestCount >= 2, "El bucle de sondeo no llegó a iterar dos veces.");

        service.StopPolling();
        Assert.False(service.IsPollingActive);

        // Deja asentar cualquier petición que hubiera quedado en vuelo iniciada antes del stop.
        await Task.Delay(pollInterval * 2);
        int countAfterStop = Volatile.Read(ref handler.RequestCount);

        // Un bucle aún vivo emitiría una petición por intervalo: exigir inmovilidad durante varios intervalos.
        var stillDeadline = DateTime.UtcNow.Add(pollInterval * 6);
        while (DateTime.UtcNow < stillDeadline)
        {
            Assert.Equal(countAfterStop, Volatile.Read(ref handler.RequestCount));
            await Task.Delay(pollInterval);
        }

        Assert.Equal(countAfterStop, Volatile.Read(ref handler.RequestCount));
        Assert.False(service.IsPollingActive);
    }

    [Fact]
    public async Task HealthRecovery_ResetsFatalErrorState_OnClientStateService()
    {
        var clientState = new ClientStateService();
        clientState.TryActivateFatalError();
        Assert.True(clientState.IsFatalErrorActive);

        var okHandler = new SuccessHealthHandler();
        using var client = new HttpClient(okHandler) { BaseAddress = new Uri("http://localhost:5000/") };
        using var service = new HealthPollingService(client, clientState);

        bool eventFired = false;
        service.OnHealthRecovered += (_, _) => eventFired = true;

        service.StartPolling();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.IsPollingActive && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.False(clientState.IsFatalErrorActive);
        Assert.True(eventFired);
        Assert.False(service.IsPollingActive);
    }

    [Fact]
    public void ConcurrentStartStop_MaintainsConsistency()
    {
        using var client = new HttpClient(new CountingHandler()) { BaseAddress = new Uri("http://localhost:5000/") };
        using var service = new HealthPollingService(client);

        Parallel.For(0, 50, _ =>
        {
            service.StartPolling();
            service.StopPolling();
        });

        Assert.False(service.IsPollingActive);
    }

    private sealed class SuccessHealthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
