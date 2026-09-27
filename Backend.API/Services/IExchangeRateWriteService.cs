using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Services;

/// <summary>8.7-M3: contrato único para escribir la tasa del día (upsert + invalidación de caché +
/// recálculo de ventas OnHold + difusión SignalR). Elimina la duplicación entre controller y job.</summary>
public interface IExchangeRateWriteService
{
    /// <summary>Upserta la tasa del día (fecha Venezuela). Devuelve true si hubo cambio persistente
    /// y se recalculó/difundió; false si la tasa ya era idéntica (no-op).</summary>
    Task<bool> UpsertTodayRateAsync(decimal roundedRate, CancellationToken cancellationToken = default);
}