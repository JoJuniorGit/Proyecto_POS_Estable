namespace Desktop.Client.Services;

public interface IExchangeRateService : IAsyncDisposable
{
    decimal CurrentRate { get; set; }
    DateTime? LastUpdated { get; }
    bool IsRateOutdated { get; }
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

    private DateTime? _updatedAtLocal;
    public DateTime UpdatedAtLocal
    {
        get
        {
            if (_updatedAtLocal.HasValue) return _updatedAtLocal.Value;
            var utc = UpdatedAt.Kind == DateTimeKind.Utc
                ? UpdatedAt
                : DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc);
            return Core.Helpers.TimeZoneHelper.ToVenezuelaTime(utc);
        }
        set => _updatedAtLocal = value;
    }
}
