using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public record ExchangeRateRecordDto(DateOnly Date, decimal Rate, DateTime UpdatedAt);

public interface IExchangeRateHistoryService
{
    Task<(decimal Rate, DateOnly Date, DateTime? UpdatedAt)> GetTodayRateWithMetadataAsync(CancellationToken cancellationToken = default);
    Task<List<ExchangeRateRecordDto>> GetHistoryAsync(int limit = 365, CancellationToken cancellationToken = default);
}
