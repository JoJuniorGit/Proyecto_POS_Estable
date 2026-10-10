using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CommandCenter.Wpf.E2ETests.Fixtures;

public sealed record E2eLoginOutcome(string? Token, bool RequiresPasswordChange);

public sealed record E2eTestProduct(string Name, string Sku);

public sealed record E2eBootstrapState(string ProductName, string ProductSku, decimal ExchangeRate);

/// <summary>Usuario creado por la API real (8.151-W5: cajero del E2E de WebSockets).</summary>
public sealed record E2eCreatedUser(int Id, string Cedula, string Name);

/// <summary>
/// Bootstrap del harness contra la API real (HttpClient simple, sin mocks): login del admin
/// sembrado, rotación de la clave forzada, tasa de cambio y producto de prueba. Todas las
/// llamadas fallan con mensaje claro incluyendo el cuerpo de la respuesta.
/// </summary>
public sealed class E2eApiClient : IDisposable
{
    // Igual a SystemSettings:MinimumClientVersion (Backend.API/appsettings.json): el
    // VersionCheckMiddleware exige el header en rutas /api/* que no sean auth/pairing.
    private const string ClientVersion = "1.0.0";

    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;

    public E2eApiClient(string baseAddress)
    {
        _baseAddress = new Uri(baseAddress, UriKind.Absolute);
        _httpClient = new HttpClient
        {
            BaseAddress = _baseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("X-Client-Version", ClientVersion);
    }

    public async Task<E2eLoginOutcome> LoginAsync(string cedula, string password, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/auth/login",
            new { Cedula = cedula, Password = password },
            bearerToken: null,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Contrato real (AuthController + ApiProblemResults): 403 con requiresPasswordChange=true
        // cuando el admin sembrado todavía debe rotar la clave.
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && ContainsRequiresPasswordChange(body))
        {
            return new E2eLoginOutcome(null, RequiresPasswordChange: true);
        }

        EnsureSuccess(response, "login", body);

        using var json = JsonDocument.Parse(body);
        var token = json.RootElement.TryGetProperty("token", out var tokenProperty)
            ? tokenProperty.GetString()
            : null;
        return new E2eLoginOutcome(token, RequiresPasswordChange: false);
    }

    public async Task ChangePasswordAsync(
        string cedula,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/auth/change-password",
            new { Cedula = cedula, CurrentPassword = currentPassword, NewPassword = newPassword },
            bearerToken: null,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "change-password", body);
    }

    public async Task<decimal> SetExchangeRateAsync(string bearerToken, decimal value, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/exchange-rate",
            new { Value = value },
            bearerToken,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "set exchange rate", body);

        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("value", out var valueProperty)
            ? valueProperty.GetDecimal()
            : value;
    }

    public async Task<E2eTestProduct> CreateTestProductAsync(string bearerToken, CancellationToken cancellationToken = default)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var name = $"Producto E2E {unique}";
        var sku = $"E2E-{unique.ToUpperInvariant()}";

        // CreateProductDto válido: costo + márgenes (precio calculado por el servicio), stock inicial
        // con movimiento de carga y datos únicos por corrida para que la búsqueda de UI sea inequívoca.
        var payload = new
        {
            Name = name,
            SKU = sku,
            Description = "Producto de prueba del harness E2E full-stack",
            CostPriceUSD = 10.00m,
            ProfitMarginRetail = 30.00m,
            ProfitMarginWholesale = 20.00m,
            MinWholesaleQuantity = 6.000m,
            HasWholesale = true,
            StockQuantity = 100.000m,
            LowStockThreshold = 5.000m,
            IsActive = true
        };

        using var response = await SendAsync(HttpMethod.Post, "api/products", payload, bearerToken, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "create test product", body);

        return new E2eTestProduct(name, sku);
    }

    /// <summary>
    /// 8.151-W5: siembra un cajero con contraseña explícita (nace sin MustChangePassword, así el
    /// login por HTTP devuelve token directo). <c>Role = 1</c> es <c>UserRole.Cashier</c>: el
    /// backend serializa enums como número.
    /// </summary>
    public async Task<E2eCreatedUser> CreateCashierAsync(
        string bearerToken,
        string cedula,
        string name,
        string password,
        CancellationToken cancellationToken = default)
    {
        var payload = new { Cedula = cedula, Name = name, Password = password, Role = 1 };
        using var response = await SendAsync(HttpMethod.Post, "api/users", payload, bearerToken, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "create cashier", body);

        using var json = JsonDocument.Parse(body);
        return new E2eCreatedUser(
            json.RootElement.GetProperty("id").GetInt32(),
            ReadString(json.RootElement, "cedula") ?? cedula,
            ReadString(json.RootElement, "name") ?? name);
    }

    /// <summary>Abre una venta real del usuario autenticado (POST /api/sales/start) y devuelve su id.</summary>
    public async Task<int> StartSaleAsync(string bearerToken, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/sales/start", payload: null, bearerToken, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "start sale", body);

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetInt32();
    }

    /// <summary>Id real del producto sembrado por el bootstrap (GET /api/products/quick-check/{sku}).</summary>
    public async Task<int> GetProductIdBySkuAsync(string bearerToken, string sku, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"api/products/quick-check/{Uri.EscapeDataString(sku)}",
            payload: null,
            bearerToken,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "product quick-check", body);

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetInt32();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUri,
        object? payload,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUri);
        if (payload != null)
        {
            request.Content = JsonContent.Create(payload);
        }

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Fallo de red en {method} {_baseAddress}{relativeUri}: {ex.Message}", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation, string body)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Bootstrap '{operation}' falló con {(int)response.StatusCode} {response.ReasonPhrase}. " +
            $"Url: {response.RequestMessage?.RequestUri}. Cuerpo: {body}");
    }

    private static bool ContainsRequiresPasswordChange(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("requiresPasswordChange", out var flag) &&
                   flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
