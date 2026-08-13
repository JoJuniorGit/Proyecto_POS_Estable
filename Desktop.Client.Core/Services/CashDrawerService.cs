using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class CashDrawerService : ICashDrawerService
{
    private readonly HttpClient _httpClient;
    private readonly UserSession? _userSession;

    public CashDrawerService(HttpClient httpClient, UserSession? userSession = null)
    {
        _httpClient = httpClient;
        _userSession = userSession;
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

        var response = await _httpClient.PostAsJsonAsync("api/cashdrawer/transaction", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CashTransactionDto>())!;
    }

    public async Task<CashAdvanceResultClientDto?> ProcessCashAdvanceAsync(
        int sessionId,
        decimal requestedAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId = null,
        string? userName = null)
    {
        var activeCashierId = cashierId ?? _userSession?.CurrentUser?.Id;
        var activeUserName = userName ?? _userSession?.CurrentUser?.Name ?? _userSession?.CurrentUser?.Cedula ?? "Usuario";

        var request = new
        {
            SessionId = sessionId,
            RequestedAmountLocal = requestedAmountLocal,
            PaymentMethodId = paymentMethodId,
            PaymentMethodName = paymentMethodName,
            IsTransfer = isTransfer,
            ExchangeRate = exchangeRate,
            CashierId = activeCashierId,
            UserName = activeUserName
        };

        var response = await _httpClient.PostAsJsonAsync("api/cashdrawer/cash-advance", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CashAdvanceResultClientDto>();
    }
}
