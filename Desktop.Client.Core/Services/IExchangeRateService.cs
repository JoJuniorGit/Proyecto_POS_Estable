namespace Desktop.Client.Services;

public interface IExchangeRateService : IAsyncDisposable
{
    decimal CurrentRate { get; set; }
    Task<(decimal Rate, DateTime? LastUpdated)> GetCurrentRateAsync();
    Task SaveRateAsync(decimal rate);
    Task<List<ExchangeRateHistoryDto>> GetHistoryAsync();
    Task<(decimal Rate, DateTime? LastUpdated)> SyncBcvAsync();
}

public class ExchangeRateHistoryDto
{
    public DateOnly Date { get; set; }
    public decimal Rate { get; set; }
    public DateTime UpdatedAt { get; set; }
}
