using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Services;
using Core.Entities;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.150-T11 (E1): composición REAL de la aplicación (Program.cs) sobre PostgreSQL real para el
/// E2E de autorizaciones remotas. Cada escenario recibe una base única <c>pos_e2e_&lt;run&gt;</c>
/// creada desde cero, migrada y sembrada por el propio arranque (DatabaseInitializer) y
/// descartada al final con DROP ... WITH (FORCE) (misma estrategia que FullStackFixture).
/// Sin TEST_POSTGRES_CONNECTION la aplicación se declara no disponible y los tests se omiten.
/// </summary>
internal sealed class AuthorizationEndToEndApplication : IAsyncDisposable
{
    internal const string TestJwtKey = "POS_E2E_Super_Secret_Key_At_Least_32_Chars_Long_2026!";
    internal const string ClientVersion = "1.0.0";
    internal const decimal DefaultExchangeRate = 50.00m;

    private const string AuthorizationHubPath = "hubs/authorization";
    private const int DropAttempts = 3;
    private const int DropRetryDelayMilliseconds = 500;

    private readonly string? _baseConnectionString;
    private readonly EnvironmentOverrideScope? _environment;
    private readonly WebApplicationFactory<Program>? _factory;
    private readonly string _databaseName;
    private readonly string _connectionString;

    private AuthorizationEndToEndApplication(
        string? baseConnectionString,
        EnvironmentOverrideScope? environment,
        WebApplicationFactory<Program>? factory,
        string databaseName,
        string connectionString)
    {
        _baseConnectionString = baseConnectionString;
        _environment = environment;
        _factory = factory;
        _databaseName = databaseName;
        _connectionString = connectionString;
    }

    public bool IsAvailable => _factory is not null;

    public string DatabaseName => _databaseName;

    public string ConnectionString => _connectionString;

    public WebApplicationFactory<Program> Factory => _factory
        ?? throw new InvalidOperationException("La aplicación E2E no está disponible (falta TEST_POSTGRES_CONNECTION).");

    public TestServer Server => Factory.Server;

    public static async Task<AuthorizationEndToEndApplication> CreateAsync(string scenario)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            // Gating del repo: sin PostgreSQL configurado el escenario se omite en silencio.
            return new AuthorizationEndToEndApplication(null, null, null, string.Empty, string.Empty);
        }

        var databaseName = BuildDatabaseName(scenario);
        var derivedBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = databaseName };
        var derivedConnection = derivedBuilder.ConnectionString;

        // Overrides del proceso antes de que Program.cs componga el host: base aislada, entorno
        // Development (migración + seeds), clave JWT pineada para emitir los tokens de prueba con
        // el MISMO TokenService real, DB_PASSWORD (Program pisa la contraseña de la cadena con la
        // env var; se alinea para que el override sea un no-op) y sync BCV deshabilitado (diseño
        // 8.16-B10) para que el scraper externo no altere la tasa a mitad del E2E.
        var environment = new EnvironmentOverrideScope(
            ("ASPNETCORE_ENVIRONMENT", "Development"),
            ("ConnectionStrings__DefaultConnection", derivedConnection),
            ("JWT_SETTINGS_KEY", TestJwtKey),
            ("DB_PASSWORD", derivedBuilder.Password),
            ("BcvSettings__AutoSyncIntervalMinutes", "0"));

        WebApplicationFactory<Program>? factory = null;
        try
        {
            await CreateFreshDatabaseAsync(baseConnectionString, databaseName);
            factory = new WebApplicationFactory<Program>();
            using (factory.CreateClient())
            {
                // Primer cliente: arranca el pipeline real, que migra y siembra la base aislada.
            }

            await VerifyIsolatedDatabaseInitializedAsync(derivedConnection, databaseName);
            return new AuthorizationEndToEndApplication(baseConnectionString, environment, factory, databaseName, derivedConnection);
        }
        catch
        {
            environment.Restore();
            factory?.Dispose();
            await TryDropDatabaseAsync(baseConnectionString, databaseName);
            throw;
        }
    }

    public HttpClient CreateHttpClient(string token)
    {
        var client = Factory.CreateClient();
        // 8.143: gate de versión del pipeline real para /api/*.
        client.DefaultRequestHeaders.Add("X-Client-Version", ClientVersion);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HubConnection CreateHubConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, AuthorizationHubPath), options =>
            {
                // TestServer no soporta WebSockets: LongPolling real con el handler del servidor.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    public string IssueToken(User user)
    {
        using var scope = Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateToken(user);
    }

    public SalesDbContext CreateSalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        return new SalesDbContext(options);
    }

    public InventoryDbContext CreateInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        return new InventoryDbContext(options);
    }

    public async Task<User> CreateUserAsync(int id, UserRole role, string name, string? password = null)
    {
        await using var db = CreateSalesDbContext();
        var user = new User
        {
            Id = id,
            Cedula = $"V-{id:D8}",
            Name = name,
            FullName = name,
            Username = $"usuario{id}",
            Role = role,
            IsActive = true,
            MustChangePassword = false,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            PasswordHash = password is null ? string.Empty : Core.Security.PasswordHasher.HashPassword(password)
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<Product> CreateProductAsync(int id, string name, decimal priceUsd, decimal priceBsS, decimal stock = 100m)
    {
        await using var db = CreateInventoryDbContext();
        var product = new Product
        {
            Id = id,
            SKU = $"E2E-{id}",
            Name = name,
            Description = "Producto del E2E 8.150",
            PriceUSD = priceUsd,
            PriceRetailUSD = priceUsd,
            PriceWholesaleUSD = priceUsd,
            PriceBsS = priceBsS,
            CostPriceUSD = priceUsd / 2m,
            Cost = priceUsd / 2m,
            StockQuantity = stock,
            IsActive = true,
            IsDeleted = false,
            IsFractional = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    /// <summary>
    /// Tasa segura para el AddItem protegido: la BCV del día si existiera (con techo 2d, como el
    /// servicio real) o el valor por defecto. Evita el rechazo por desvío ≥ ±100% si una corrida
    /// llegara a habilitar el scraper.
    /// </summary>
    public async Task<decimal> GetSafeExchangeRateAsync()
    {
        await using var db = CreateInventoryDbContext();
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var record = await db.ExchangeRateHistory.AsNoTracking().FirstOrDefaultAsync(row => row.Date == today);
        return record is { Rate: > 0m }
            ? Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(record.Rate)
            : DefaultExchangeRate;
    }

    public async ValueTask DisposeAsync()
    {
        _environment?.Restore();
        _factory?.Dispose();
        if (!string.IsNullOrWhiteSpace(_baseConnectionString) && !string.IsNullOrWhiteSpace(_databaseName))
        {
            await TryDropDatabaseAsync(_baseConnectionString, _databaseName);
        }
    }

    private static string BuildDatabaseName(string scenario)
    {
        var suffix = Environment.GetEnvironmentVariable("E2E_DB_SUFFIX");
        var name = string.IsNullOrWhiteSpace(suffix)
            ? $"pos_e2e_{scenario}_{Guid.NewGuid():N}"
            : $"pos_e2e_{scenario}_{suffix}_{Guid.NewGuid():N}";
        return name.Length <= 63 ? name.ToLowerInvariant() : name[..63].ToLowerInvariant();
    }

    private static async Task CreateFreshDatabaseAsync(string baseConnectionString, string databaseName)
    {
        var maintenanceConnection = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "postgres" }.ConnectionString;
        await using var connection = new NpgsqlConnection(maintenanceConnection);
        try
        {
            await connection.OpenAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "No se pudo conectar a PostgreSQL con TEST_POSTGRES_CONNECTION " +
                $"(Host={new NpgsqlConnectionStringBuilder(baseConnectionString).Host}). " +
                $"Verifique el servicio y las credenciales: {ex.Message}", ex);
        }

        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    private static async Task VerifyIsolatedDatabaseInitializedAsync(string derivedConnectionString, string databaseName)
    {
        // El .env raíz (DotNetEnv, Development) podría pisar variables del proceso: si el backend
        // hubiera migrado otra base, los tests operarían contra una BD no aislada. Este chequeo
        // convierte ese falso verde en un error explícito.
        await using var connection = new NpgsqlConnection(derivedConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('public.\"Users\"') IS NOT NULL", connection);
        var initialized = (bool)(await command.ExecuteScalarAsync() ?? false);
        if (!initialized)
        {
            throw new InvalidOperationException(
                $"La base aislada '{databaseName}' no tiene el esquema migrado tras el arranque; " +
                "posible override de ConnectionStrings__DefaultConnection en un .env (Development).");
        }
    }

    private static async Task TryDropDatabaseAsync(string baseConnectionString, string databaseName)
    {
        var maintenanceConnection = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "postgres" }.ConnectionString;
        for (var attempt = 1; attempt <= DropAttempts; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(maintenanceConnection);
                await connection.OpenAsync();
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
                await drop.ExecuteNonQueryAsync();
                return;
            }
            catch (Exception)
            {
                // Limpieza best-effort (espejo de FullStackFixture): un drop fallido no debe
                // enmascarar el resultado del escenario; el tercer intento se rinde.
                if (attempt < DropAttempts)
                {
                    await Task.Delay(DropRetryDelayMilliseconds);
                }
            }
        }
    }

    /// <summary>Snapshot y restauración de las variables de entorno intervenidas por el E2E.</summary>
    private sealed class EnvironmentOverrideScope
    {
        private readonly (string Name, string? Previous)[] _previous;

        public EnvironmentOverrideScope(params (string Name, string? Value)[] values)
        {
            _previous = new (string, string?)[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                var (name, value) = values[index];
                _previous[index] = (name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Restore()
        {
            foreach (var (name, previous) in _previous)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }
}

/// <summary>
/// 8.150-T11 (E1): helpers HTTP/SignalR del E2E; todas las llamadas cruzan el pipeline real
/// (TestServer del WebApplicationFactory con autenticación JWT Bearer y gate de versión).
/// </summary>
internal static class AuthorizationEndToEndApi
{
    public const string AuthorizationTokenHeader = "X-Authorization-Token";

    public static RequestAuthorizationContract BuildManualPriceOverrideContract(
        int saleId,
        int productId,
        string productName,
        decimal quantity,
        decimal customUnitPriceUsd,
        decimal customUnitPriceLocal,
        string terminal) => new()
        {
            SaleId = saleId,
            ProductId = productId,
            ProductName = productName,
            Quantity = quantity,
            CustomUnitPriceUsd = customUnitPriceUsd,
            CustomUnitPriceLocal = customUnitPriceLocal,
            Terminal = terminal
        };

    public static object BuildAddItemPayload(
        int productId,
        decimal quantity,
        decimal exchangeRate,
        decimal customUnitPriceUsd,
        decimal customUnitPriceLocal) => new
        {
            productId,
            quantity,
            exchangeRate,
            customUnitPriceUsd,
            customUnitPriceLocal
        };

    public static async Task<int> StartSaleAsync(HttpClient cashierClient)
    {
        using var response = await cashierClient.PostAsync("/api/sales/start", content: null);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    public static async Task<int> CreateRequestAsync(HttpClient client, RequestAuthorizationContract contract)
    {
        using var response = await client.PostAsJsonAsync("/api/authorizations", contract);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestId").GetInt32();
    }

    public static async Task<JsonElement> ResolveAsync(HttpClient elevatedClient, int requestId, bool approved, string? reason)
    {
        using var response = await elevatedClient.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/resolve",
            new { approved, reason });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Task<HttpResponseMessage> CancelAsync(HttpClient requesterClient, int requestId) =>
        requesterClient.PostAsync($"/api/authorizations/{requestId}/cancel", content: null);

    public static Task<HttpResponseMessage> LocalResolveAsync(
        HttpClient client,
        int requestId,
        string username,
        string password,
        string? reason) =>
        client.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/local-resolve",
            new { username, password, reason });

    public static async Task<JsonElement> GetStatusAsync(HttpClient requesterClient, int requestId)
    {
        using var response = await requesterClient.GetAsync($"/api/authorizations/{requestId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<HttpResponseMessage> SendAddItemAsync(
        HttpClient client,
        int saleId,
        object payload,
        string? authorizationToken,
        string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sales/{saleId}/items")
        {
            Content = JsonContent.Create(payload)
        };
        if (authorizationToken is not null)
        {
            request.Headers.Add(AuthorizationTokenHeader, authorizationToken);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    public static string ReadRequiredToken(JsonElement payload)
    {
        var token = payload.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token), "El payload debe incluir el token efímero.");
        return token!;
    }

    public static async Task AssertPriceOverrideForbiddenAsync(HttpResponseMessage response)
    {
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor.",
            body.GetProperty("detail").GetString());
        Assert.True(body.GetProperty("authorizationRequired").GetBoolean());
        Assert.Equal("ManualPriceOverride", body.GetProperty("authorizationAction").GetString());
    }
}
