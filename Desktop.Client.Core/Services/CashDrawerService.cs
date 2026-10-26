using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class CashDrawerService : ICashDrawerService
{
    private readonly HttpClient _httpClient;
    private readonly object _idempotencyLock = new();
    private string? _openSignature;
    private string? _openIdempotencyKey;
    private string? _closeSignature;
    private string? _closeIdempotencyKey;

    public CashDrawerService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CashDrawerSessionDto?> GetActiveSessionAsync()
    {
        var response = await _httpClient.GetAsync("api/cashdrawer/active-session");
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CashDrawerSessionDto>();
    }

    public async Task<CashDrawerSessionDto> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate, decimal openingBalanceUsd = 0m)
    {
        var request = new
        {
            OpeningBalanceLocal = openingBalanceLocal,
            OpeningBalanceUsd = openingBalanceUsd,
            CurrentExchangeRate = currentExchangeRate
        };
        var signature = JsonSerializer.Serialize(request);
        var idempotencyKey = GetStableKey(isOpen: true, signature);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/cashdrawer/open")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await _httpClient.SendAsync(httpRequest);
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<CashDrawerSessionDto>())!;
        CompleteStableKey(isOpen: true, signature);
        return session;
    }

    public Task<CashDrawerSessionDto> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate)
        => CloseSessionAsync(actualClosingBalanceLocal, currentExchangeRate, actualClosingBalanceUsd: null);

    public async Task<CashDrawerSessionDto> CloseSessionAsync(
        decimal actualClosingBalanceLocal,
        decimal currentExchangeRate,
        decimal? actualClosingBalanceUsd)
    {
        // T4 (Paso 13 / 8.188, S2): el campo USD solo viaja cuando el operador informó el conteo;
        // sin él, el servidor conserva el comportamiento previo y deja ClosingBalanceUsd en null.
        var request = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["actualClosingBalanceLocal"] = actualClosingBalanceLocal,
            ["currentExchangeRate"] = currentExchangeRate
        };
        if (actualClosingBalanceUsd.HasValue)
        {
            request["actualClosingBalanceUsd"] = actualClosingBalanceUsd.Value;
        }

        var signature = JsonSerializer.Serialize(request);
        var idempotencyKey = GetStableKey(isOpen: false, signature);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/cashdrawer/close")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await _httpClient.SendAsync(httpRequest);
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<CashDrawerSessionDto>())!;
        CompleteStableKey(isOpen: false, signature);
        return session;
    }

    private string GetStableKey(bool isOpen, string signature)
    {
        lock (_idempotencyLock)
        {
            ref var storedSignature = ref (isOpen ? ref _openSignature : ref _closeSignature);
            ref var storedKey = ref (isOpen ? ref _openIdempotencyKey : ref _closeIdempotencyKey);
            if (!string.Equals(storedSignature, signature, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(storedKey))
            {
                storedSignature = signature;
                storedKey = Guid.NewGuid().ToString("N");
            }

            return storedKey;
        }
    }

    private void CompleteStableKey(bool isOpen, string signature)
    {
        lock (_idempotencyLock)
        {
            ref var storedSignature = ref (isOpen ? ref _openSignature : ref _closeSignature);
            ref var storedKey = ref (isOpen ? ref _openIdempotencyKey : ref _closeIdempotencyKey);
            if (string.Equals(storedSignature, signature, StringComparison.Ordinal))
            {
                storedSignature = null;
                storedKey = null;
            }
        }
    }

    public async Task<decimal> GetCurrentBalanceLocalAsync(int sessionId)
    {
        var response = await _httpClient.GetAsync($"api/cashdrawer/current-balance?sessionId={sessionId}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<decimal>();
    }

    public async Task<System.Collections.Generic.List<CashTransactionDto>> GetHistoryAsync(int limit = 300)
    {
        var response = await _httpClient.GetAsync($"api/cashdrawer/history?limit={limit}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<System.Collections.Generic.List<CashTransactionDto>>())!;
    }

    public async Task<CashTransactionDto> AddTransactionAsync(int sessionId, decimal amountLocal, CashTransactionType type, CashTransactionSource source, string description, decimal exchangeRate)
    {
        var request = new
        {
            SessionId = sessionId,
            AmountLocal = amountLocal,
            Type = type,
            Source = source,
            Description = description,
            ExchangeRate = exchangeRate
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/cashdrawer/transaction")
        {
            Content = JsonContent.Create(request)
        };
        // 8.149 (SRE-02): el backend exige Idempotency-Key; el retry del ResilienceHandler reenvia el
        // MISMO request, por lo que la clave se mantiene estable dentro del intento logico.
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await _httpClient.SendAsync(httpRequest);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CashTransactionDto>())!;
    }

    public async Task<decimal?> GetAdvanceCommissionAsync(bool isTransfer, System.Threading.CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"api/cashdrawer/advance-commission?isTransfer={(isTransfer ? "true" : "false")}", cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<AdvanceCommissionClientDto>(cancellationToken: cancellationToken);

        return dto != null && dto.Percentage > 0 ? dto.Percentage : null;
    }

    public async Task<CashAdvanceResultClientDto?> ProcessCashAdvanceAsync(
        int sessionId,
        decimal requestedAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate)
    {
        // 8.154 (SEC-03): la identidad del adelanto la resuelve el backend desde el token JWT;
        // el cliente solo envia los datos del movimiento.
        var request = new
        {
            SessionId = sessionId,
            RequestedAmountLocal = requestedAmountLocal,
            PaymentMethodId = paymentMethodId,
            PaymentMethodName = paymentMethodName,
            IsTransfer = isTransfer,
            ExchangeRate = exchangeRate
        };

        var response = await _httpClient.PostAsJsonAsync("api/cashdrawer/cash-advance", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CashAdvanceResultClientDto>();
    }

    public async Task<PettyCashExpenseClientDto> RegisterPettyCashExpenseAsync(
        PettyCashExpenseClientRequest request,
        string? authorizationToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/cashdrawer/petty-cash")
        {
            Content = JsonContent.Create(request)
        };
        if (!string.IsNullOrWhiteSpace(authorizationToken))
        {
            httpRequest.Headers.TryAddWithoutValidation("X-Authorization-Token", authorizationToken);
        }

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowPettyCashErrorAsync(response, "No se pudo registrar el gasto de caja chica.");
        }

        return (await response.Content.ReadFromJsonAsync<PettyCashExpenseClientDto>(cancellationToken: cancellationToken))!;
    }

    public async Task<PettyCashExpenseClientDto> CancelPettyCashExpenseAsync(
        int expenseId,
        string reason,
        string? authorizationToken = null,
        CancellationToken cancellationToken = default)
    {
        var request = new PettyCashCancelClientRequest { Reason = reason };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/cashdrawer/petty-cash/{expenseId}/cancel")
        {
            Content = JsonContent.Create(request)
        };
        if (!string.IsNullOrWhiteSpace(authorizationToken))
        {
            httpRequest.Headers.TryAddWithoutValidation("X-Authorization-Token", authorizationToken);
        }

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowPettyCashErrorAsync(response, "No se pudo anular el gasto de caja chica.");
        }

        return (await response.Content.ReadFromJsonAsync<PettyCashExpenseClientDto>(cancellationToken: cancellationToken))!;
    }

    public async Task<IReadOnlyList<PettyCashExpenseClientDto>> GetPettyCashExpensesAsync(
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/cashdrawer/petty-cash?sessionId={sessionId}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowPettyCashErrorAsync(response, "No se pudieron consultar los gastos de caja chica.");
        }

        var rows = await response.Content.ReadFromJsonAsync<List<PettyCashExpenseClientDto>>(
            cancellationToken: cancellationToken);
        return rows ?? new List<PettyCashExpenseClientDto>();
    }

    public async Task<byte[]?> GetPettyCashVoucherAsync(
        int expenseId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/cashdrawer/petty-cash/{expenseId}/receipt",
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowPettyCashErrorAsync(response, "No se pudo generar el vale de caja chica.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<PettyCashPhysicalBalancesClientDto> GetPhysicalBalancesAsync(
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/cashdrawer/physical-balances?sessionId={sessionId}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowPettyCashErrorAsync(response, "No se pudieron consultar los saldos físicos de caja.");
        }

        return await response.Content.ReadFromJsonAsync<PettyCashPhysicalBalancesClientDto>(
            cancellationToken: cancellationToken) ?? new PettyCashPhysicalBalancesClientDto();
    }

    /// <summary>
    /// T4 (S4): traduce el 403 ProblemDetails al contrato tipado. Solo cuando el backend declara
    /// authorizationAction=PettyCashExpense y el contexto exacto está completo se lanza la
    /// excepción tipada; cualquier otro caso conserva la excepción genérica de autorización.
    /// </summary>
    private static async Task ThrowPettyCashErrorAsync(HttpResponseMessage response, string fallback)
    {
        string body = await response.Content.ReadAsStringAsync();
        string message = ApiErrorParser.FromBody(body, fallback);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden
            && ApiErrorParser.TryGetAuthorizationRequirement(body, out var authorizationAction))
        {
            if (string.Equals(authorizationAction, "PettyCashExpense", System.StringComparison.OrdinalIgnoreCase)
                && TryParsePettyCashContext(body, out var context))
            {
                throw new PettyCashAuthorizationException(message, context!);
            }

            throw new AuthorizationRequiredException(message, authorizationAction);
        }

        throw new System.InvalidOperationException(message);
    }

    private static bool TryParsePettyCashContext(
        string body,
        out PettyCashExpenseAuthorizationRequestContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetJsonProperty(root, "authorizationContext", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetJsonProperty(payload, "sessionId", out var sessionElement)
                || !sessionElement.TryGetInt32(out int sessionId)
                || !TryGetJsonProperty(payload, "currency", out var currencyElement)
                || !currencyElement.TryGetInt32(out int currencyValue)
                || !System.Enum.IsDefined(typeof(PettyCashCurrencyClient), currencyValue)
                || !TryGetJsonProperty(payload, "amountBss", out var amountBsSElement)
                || !amountBsSElement.TryGetDecimal(out decimal amountBsS)
                || !TryGetJsonProperty(payload, "amountUsd", out var amountUsdElement)
                || !amountUsdElement.TryGetDecimal(out decimal amountUsd)
                || !TryGetJsonProperty(payload, "category", out var categoryElement)
                || !categoryElement.TryGetInt32(out int categoryValue)
                || !System.Enum.IsDefined(typeof(PettyCashCategoryClient), categoryValue))
            {
                return false;
            }

            context = new PettyCashExpenseAuthorizationRequestContext
            {
                SessionId = sessionId,
                Currency = (PettyCashCurrencyClient)currencyValue,
                AmountBsS = amountBsS,
                AmountUsd = amountUsd,
                Category = (PettyCashCategoryClient)categoryValue,
                Terminal = System.Environment.MachineName
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
