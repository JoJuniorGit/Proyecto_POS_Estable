using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;

namespace Core.Interfaces;

/// <summary>
/// 8.180-T1 (S2-S4): ciclo de vida de las sesiones de conteo físico ciego (Paso 6 del roadmap).
/// Las lecturas/servicio NO exponen teórico/costo fuera del review; los conflictos de estado se
/// señalan como <see cref="System.InvalidOperationException"/> (409) y la ausencia como
/// <see cref="System.Collections.Generic.KeyNotFoundException"/> (404).
/// </summary>
public interface IStockCountService
{
    Task<StockCountSessionDto> CreateAsync(CreateStockCountRequestDto request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockCountSessionDto>> ListAsync(StockCountStatus? status = null, CancellationToken cancellationToken = default);

    Task<StockCountDetailDto> GetDetailAsync(int id, CancellationToken cancellationToken = default);

    Task<StockCountItemDto> ScanAsync(int id, ScanStockCountRequestDto request, CancellationToken cancellationToken = default);

    Task<StockCountItemDto> SetCountedQuantityAsync(int id, int productId, SetCountedQuantityRequestDto request, CancellationToken cancellationToken = default);

    Task RemoveItemAsync(int id, int productId, CancellationToken cancellationToken = default);

    Task<StockCountReviewDto> ReviewAsync(int id, CancellationToken cancellationToken = default);

    Task<StockCountSessionDto> CloseAsync(int id, CancellationToken cancellationToken = default);

    Task<StockCountSessionDto> ApplyAsync(int id, CancellationToken cancellationToken = default);

    Task<StockCountSessionDto> CancelAsync(int id, CancellationToken cancellationToken = default);
}
