using System;
using System.Collections.Generic;
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

/// <summary>Gasto de caja chica del listado real (subconjunto estable del contrato del servidor).</summary>
public sealed record E2ePettyCashExpense(
    int Id,
    long VoucherNumber,
    decimal AmountBsS,
    decimal AmountUsd,
    string Concept,
    int Status,
    string? CancellationReason);

/// <summary>Descarga binaria del vale (bytes + content type) verificada contra la API real.</summary>
public sealed record E2eVoucherDownload(byte[] Bytes, string? ContentType);

/// <summary>Totales reales del preview de cobro (sin persistir) para sembrar apartados.</summary>
public sealed record E2eCheckoutPreview(decimal TotalUSD, decimal TotalBsS);

/// <summary>Fila del listado real de mercancía pendiente por retirar.</summary>
public sealed record E2ePendingPickup(int SaleId, decimal TotalUnits, decimal DeliveredUnits, string DeliveryStatus);

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

    /// <summary>
    /// Abre una sesión de caja por la API real (POST /api/cashdrawer/open) y devuelve su id. Es el
    /// sustrato de los flujos que exigen turno activo (p. ej. caja chica): la UI no ofrece una
    /// acción de apertura, la sesión nace del cobro o de esta superficie administrativa.
    /// </summary>
    public async Task<int> OpenCashDrawerSessionAsync(
        string bearerToken,
        decimal openingBalanceLocal,
        decimal exchangeRate,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/cashdrawer/open",
            new { OpeningBalanceLocal = openingBalanceLocal, CurrentExchangeRate = exchangeRate },
            bearerToken,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "open cash drawer session", body);

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetInt32();
    }

    /// <summary>Gastos del turno (activos y anulados) según el servidor real.</summary>
    public async Task<IReadOnlyList<E2ePettyCashExpense>> GetPettyCashExpensesAsync(
        string bearerToken,
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"api/cashdrawer/petty-cash?sessionId={sessionId}",
            payload: null,
            bearerToken,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "get petty cash expenses", body);

        using var json = JsonDocument.Parse(body);
        var expenses = new List<E2ePettyCashExpense>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            expenses.Add(ParsePettyCashExpense(item));
        }

        return expenses;
    }

    /// <summary>
    /// Anula un gasto de caja chica por la API real (POST .../cancel) con motivo obligatorio y
    /// devuelve el estado resultante. Es el MISMO endpoint que consume el botón de anulación de la
    /// fila; la grilla <c>PerformantDataGrid</c> no expone sus celdas a UIA, por lo que los flujos
    /// E2E ejercen esta acción contra la API y verifican su reflejo en el panel.
    /// </summary>
    public async Task<E2ePettyCashExpense> CancelPettyCashExpenseAsync(
        string bearerToken,
        int expenseId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"api/cashdrawer/petty-cash/{expenseId}/cancel",
            new { Reason = reason },
            bearerToken,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "cancel petty cash expense", body);

        using var json = JsonDocument.Parse(body);
        return ParsePettyCashExpense(json.RootElement);
    }

    /// <summary>
    /// Descarga el vale PDF del gasto contra el endpoint real (mismo camino del botón de la UI).
    /// Un estado no exitoso falla con el cuerpo de la respuesta para diagnóstico.
    /// </summary>
    public async Task<E2eVoucherDownload> DownloadPettyCashVoucherAsync(
        string bearerToken,
        int expenseId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"api/cashdrawer/petty-cash/{expenseId}/receipt",
            payload: null,
            bearerToken,
            cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"La descarga del vale de caja chica #{expenseId} falló con {(int)response.StatusCode} {response.ReasonPhrase}. " +
                $"Cuerpo: {System.Text.Encoding.UTF8.GetString(bytes)}");
        }

        return new E2eVoucherDownload(bytes, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Crea un cliente real (el apartado exige cliente con cédula/teléfono válidos).</summary>
    public async Task<int> CreateCustomerAsync(
        string bearerToken,
        string cedula,
        string name,
        string phone,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/sales/customers",
            new { CedulaOrRif = cedula, Name = name, Phone = phone },
            bearerToken,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "create customer", body);

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetInt32();
    }

    /// <summary>Asocia el cliente real a la venta en curso (PUT /api/sales/{id}/customer).</summary>
    public async Task SetSaleCustomerAsync(string bearerToken, int saleId, int customerId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            $"api/sales/{saleId}/customer",
            new { CustomerId = customerId },
            bearerToken,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "set sale customer", body);
    }

    /// <summary>Agrega una línea real al carrito (POST /api/sales/{id}/items).</summary>
    public async Task AddItemAsync(
        string bearerToken,
        int saleId,
        int productId,
        decimal quantity,
        decimal exchangeRate,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"api/sales/{saleId}/items",
            new { ProductId = productId, Quantity = quantity, ExchangeRate = exchangeRate },
            bearerToken,
            cancellationToken,
            idempotencyKey: Guid.NewGuid().ToString("N"));
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "add sale item", body);
    }

    /// <summary>Totales del preview de cobro real, sin persistir la venta.</summary>
    public async Task<E2eCheckoutPreview> PreviewCheckoutAsync(
        string bearerToken,
        int saleId,
        decimal exchangeRate,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"api/sales/{saleId}/checkout-preview",
            new { ExchangeRate = exchangeRate, Payments = Array.Empty<object>() },
            bearerToken,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "checkout preview", body);

        using var json = JsonDocument.Parse(body);
        return new E2eCheckoutPreview(
            json.RootElement.GetProperty("totalUSD").GetDecimal(),
            json.RootElement.GetProperty("totalBsS").GetDecimal());
    }

    /// <summary>
    /// Completa la venta como apartado pagado (IsPendingPickup): efectivo con redondeo de caja, pago
    /// al 100%. El cliente real ya debe estar asociado a la venta.
    /// </summary>
    public async Task CompletePendingPickupAsync(
        string bearerToken,
        int saleId,
        decimal exchangeRate,
        E2eCheckoutPreview preview,
        CancellationToken cancellationToken = default)
    {
        decimal paidBsS = Math.Ceiling(preview.TotalBsS);
        decimal roundingAdjustment = paidBsS - preview.TotalBsS;

        using var response = await SendAsync(
            HttpMethod.Post,
            $"api/sales/{saleId}/complete",
            new
            {
                ExchangeRate = exchangeRate,
                RoundingAdjustment = roundingAdjustment,
                IsPendingPickup = true,
                Payments = new[]
                {
                    new
                    {
                        PaymentMethodId = 1,
                        Amount = preview.TotalUSD,
                        AmountBsS = paidBsS,
                        ExchangeRate = exchangeRate
                    }
                }
            },
            bearerToken,
            cancellationToken,
            idempotencyKey: Guid.NewGuid().ToString("N"));
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "complete pending pickup", body);
    }

    /// <summary>Apartados reales del servidor (mercancía en custodia, con unidades entregadas).</summary>
    public async Task<IReadOnlyList<E2ePendingPickup>> GetPendingPickupsAsync(
        string bearerToken,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "api/sales/pending-pickups?limit=200&offset=0",
            payload: null,
            bearerToken,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, "get pending pickups", body);

        using var json = JsonDocument.Parse(body);
        var rows = new List<E2ePendingPickup>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            rows.Add(new E2ePendingPickup(
                item.GetProperty("saleId").GetInt32(),
                item.GetProperty("totalUnits").GetDecimal(),
                item.GetProperty("deliveredUnits").GetDecimal(),
                ReadString(item, "deliveryStatus") ?? string.Empty));
        }

        return rows;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static E2ePettyCashExpense ParsePettyCashExpense(JsonElement item) => new(
        item.GetProperty("id").GetInt32(),
        item.GetProperty("voucherNumber").GetInt64(),
        item.GetProperty("amountBsS").GetDecimal(),
        item.GetProperty("amountUsd").GetDecimal(),
        ReadString(item, "concept") ?? string.Empty,
        item.GetProperty("status").GetInt32(),
        ReadString(item, "cancellationReason"));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUri,
        object? payload,
        string? bearerToken,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, relativeUri);
        if (payload != null)
        {
            request.Content = JsonContent.Create(payload);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
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
