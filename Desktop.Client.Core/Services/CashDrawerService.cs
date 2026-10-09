using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class CashDrawerService : ICashDrawerService
{
    private readonly HttpClient _httpClient;

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

    public async Task<CashDrawerSessionDto> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate)
    {
        var request = new { OpeningBalanceLocal = openingBalanceLocal, CurrentExchangeRate = currentExchangeRate };
        var response = await _httpClient.PostAsJsonAsync("api/cashdrawer/open", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CashDrawerSessionDto>())!;
    }

    public async Task<CashDrawerSessionDto> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate)
    {
        var request = new { ActualClosingBalanceLocal = actualClosingBalanceLocal, CurrentExchangeRate = currentExchangeRate };
        var response = await _httpClient.PostAsJsonAsync("api/cashdrawer/close", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CashDrawerSessionDto>())!;
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
}
