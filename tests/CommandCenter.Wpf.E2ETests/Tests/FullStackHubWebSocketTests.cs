using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommandCenter.Wpf.E2ETests.Fixtures;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// R9 (8.151, design D8): WebSockets reales contra el backend real del harness full-stack. Sin
/// FlaUI/UIA: dos clientes SignalR reales (cajero y admin) se conectan SIN forzar transporte y el
/// log del propio cliente debe demostrar que el transporte negociado fue WebSockets; después se
/// prueba el roundtrip real create/resolve con entrega del token al solicitante. Gateado por
/// <c>E2E_POSTGRES_CONNECTION</c> exactamente como el resto de la suite: sin la variable local
/// retorna en silencio; en CI el fixture falla cerrado.
/// </summary>
public class FullStackHubWebSocketTests : IClassFixture<FullStackFixture>
{
    private const string AuthorizationHubPath = "hubs/authorization";
    private const string RequestAuthorizationMethod = "RequestAuthorization";
    private const string ResolveAuthorizationMethod = "ResolveAuthorization";
    private const string RequestedEvent = "AuthorizationRequested";
    private const string ResolvedEvent = "AuthorizationResolved";

    /// <summary>Marcador del transporte elegido que emite el cliente SignalR real (Microsoft.AspNetCore.Http.Connections).</summary>
    private const string WebSocketTransportMarker = "Starting transport 'WebSockets'";

    private readonly FullStackFixture _fixture;

    public FullStackHubWebSocketTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HubConnections_NegotiateWebSockets_AndRequesterReceivesTokenOnRemoteResolve()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        await _fixture.InitializeAsync();

        try
        {
            var bootstrap = _fixture.Bootstrap
                ?? throw new InvalidOperationException("El bootstrap del harness no produjo estado inicial.");
            var adminToken = _fixture.AdminToken
                ?? throw new InvalidOperationException("El bootstrap no expuso el token real del admin.");

            using var api = new E2eApiClient(_fixture.BackendBaseAddress);

            // Cajero real por API: la clave explícita nace sin MustChangePassword, así el login
            // por HTTP devuelve token directo para el hub.
            const string cashierPassword = "E2eCashier!2026";
            var cashierCedula = $"V-WS{Guid.NewGuid():N}"[..12];
            var cashier = await api.CreateCashierAsync(adminToken, cashierCedula, "Cajero E2E WebSockets", cashierPassword);
            var cashierLogin = await api.LoginAsync(cashier.Cedula, cashierPassword);
            var cashierToken = cashierLogin.Token
                ?? throw new InvalidOperationException($"El login del cajero '{cashier.Cedula}' no devolvió token.");

            var saleId = await api.StartSaleAsync(cashierToken);
            var productId = await api.GetProductIdBySkuAsync(adminToken, bootstrap.ProductSku);

            var cashierLogs = new CapturingLoggerProvider();
            var adminLogs = new CapturingLoggerProvider();
            await using var cashierHub = BuildHubConnection(cashierToken, cashierLogs);
            await using var adminHub = BuildHubConnection(adminToken, adminLogs);

            var requested = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolved = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            adminHub.On<JsonElement>(RequestedEvent, payload => requested.TrySetResult(payload));
            cashierHub.On<JsonElement>(ResolvedEvent, payload => resolved.TrySetResult(payload));

            await cashierHub.StartAsync();
            await adminHub.StartAsync();

            // SIN especificar Transports: el cliente negocia con sus transportes por defecto y el
            // log real demuestra cuál eligió. En Kestrel local el resultado obligatorio es WebSockets.
            AssertWebSocketTransport(cashierLogs, "cajero");
            AssertWebSocketTransport(adminLogs, "admin");

            var create = await cashierHub.InvokeAsync<JsonElement>(
                RequestAuthorizationMethod,
                new
                {
                    saleId,
                    productId,
                    productName = bootstrap.ProductName,
                    quantity = 2m,
                    customUnitPriceUsd = 9.99m,
                    customUnitPriceLocal = 1234.5m,
                    terminal = "Caja-WS-E2E"
                });
            Assert.True(
                create.GetProperty("success").GetBoolean(),
                $"La solicitud del cajero por el hub fue rechazada: {create.GetRawText()}");
            var requestId = create.GetProperty("requestId").GetInt32();
            Assert.Equal("Pending", create.GetProperty("status").GetString());

            // El elevado recibe el push de creación (grupo role:elevated) por su propia conexión real.
            var requestedPayload = await requested.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(requestId, requestedPayload.GetProperty("requestId").GetInt32());
            Assert.Equal("ManualPriceOverride", requestedPayload.GetProperty("actionType").GetString());

            var resolve = await adminHub.InvokeAsync<JsonElement>(ResolveAuthorizationMethod, requestId, true, (string?)null);
            Assert.True(
                resolve.GetProperty("success").GetBoolean(),
                $"La resolución del admin por el hub fue rechazada: {resolve.GetRawText()}");
            Assert.Equal("Approved", resolve.GetProperty("status").GetString());

            // El solicitante recibe el cierre con el token efímero sobre SU conexión WebSocket real.
            var resolvedPayload = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(requestId, resolvedPayload.GetProperty("requestId").GetInt32());
            Assert.Equal("Approved", resolvedPayload.GetProperty("status").GetString());
            Assert.True(resolvedPayload.GetProperty("approved").GetBoolean());
            Assert.Equal("Remote", resolvedPayload.GetProperty("resolutionMode").GetString());
            var authorizationToken = resolvedPayload.GetProperty("token").GetString();
            Assert.False(
                string.IsNullOrWhiteSpace(authorizationToken),
                $"El solicitante no recibió el token efímero de autorización: {resolvedPayload.GetRawText()}");
        }
        finally
        {
            await _fixture.TeardownAsync();
        }

        Assert.False(
            await _fixture.DatabaseExistsAsync(),
            $"La base aislada '{_fixture.DatabaseName}' no fue eliminada por el teardown.");
        Assert.Empty(_fixture.TeardownErrors);
    }

    /// <summary>
    /// Conexión real al hub del backend del fixture con el token del usuario que corresponda.
    /// Deliberadamente NO se especifica <c>Transports</c>: el cliente debe negociar WebSockets por
    /// sí solo (esa es la propiedad bajo prueba del R9).
    /// </summary>
    private HubConnection BuildHubConnection(string accessToken, CapturingLoggerProvider logs)
    {
        var hubUri = new Uri(new Uri(_fixture.BackendBaseAddress), AuthorizationHubPath);

        return new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(logs);
            })
            .Build();
    }

    private static void AssertWebSocketTransport(CapturingLoggerProvider logs, string clientLabel)
    {
        // "Starting transport '<X>'" lo emite HttpConnection SOLO para el transporte seleccionado;
        // si WebSockets hubiera fallado, aparecería una línea de arranque de otro transporte.
        var startedTransportLines = logs.Messages
            .Where(message => message.Contains("Starting transport '", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            startedTransportLines.Any(line => line.Contains(WebSocketTransportMarker, StringComparison.Ordinal)),
            $"El cliente SignalR '{clientLabel}' no inició el transporte WebSockets. " +
            $"Líneas 'Starting transport' capturadas: [{string.Join(" | ", startedTransportLines)}]");

        Assert.True(
            startedTransportLines.All(line => line.Contains(WebSocketTransportMarker, StringComparison.Ordinal)),
            $"El cliente SignalR '{clientLabel}' intentó un transporte distinto de WebSockets. " +
            $"Líneas 'Starting transport' capturadas: [{string.Join(" | ", startedTransportLines)}]");
    }

    /// <summary>
    /// Provider mínimo que retiene los mensajes formateados del cliente SignalR real (niveles
    /// Trace..Critical) para poder observar el transporte negociado sin forzarlo.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public string[] Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _messages;

            public CapturingLogger(ConcurrentQueue<string> messages)
            {
                _messages = messages;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _messages.Enqueue($"{logLevel}|{eventId.Name}|{formatter(state, exception)}");
        }
    }
}
