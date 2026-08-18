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
        using var service = new HealthPollingService(client);

        service.StartPolling();
        Assert.True(service.IsPollingActive);

        // Espera (con límite) a que el bucle itere al menos dos veces: prueba que está vivo y avanzando.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (handler.RequestCount < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(handler.RequestCount >= 2, "El bucle de sondeo no llegó a iterar dos veces.");

        service.StopPolling();
        Assert.False(service.IsPollingActive);

        // Deja asentar cualquier petición que hubiera quedado en vuelo iniciada antes del stop.
        await Task.Delay(300);
        int countAfterStop = handler.RequestCount;

        // Espera más que el intervalo de 3s: si el bucle siguiera vivo, emitiría otra petición.
        await Task.Delay(3500);

        Assert.Equal(countAfterStop, handler.RequestCount);
        Assert.False(service.IsPollingActive);
    }

    [Fact]
    public void StopPolling_IsIdempotent_AndRestartUsesFreshToken()
    {
        using var client = new HttpClient(new CountingHandler()) { BaseAddress = new Uri("http://localhost:5000/") };
        using var service = new HealthPollingService(client);

        service.StartPolling();
        service.StopPolling();
        service.StopPolling(); // segunda llamada: no-op, no debe lanzar
        Assert.False(service.IsPollingActive);

        // Reinicio con CTS fresco: no debe lanzar ObjectDisposedException.
        service.StartPolling();
        Assert.True(service.IsPollingActive);
        service.StopPolling();
        Assert.False(service.IsPollingActive);

        service.Dispose(); // seguro y sin excepciones
        Assert.False(service.IsPollingActive);
    }
}
