using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;

namespace Desktop.Client.Services;

/// <summary>
/// Client contract for the blind physical stock count API. Detail/scan responses never contain
/// theoretical stock or cost; the review endpoint is the only difference surface and is called
/// only for Admin/Manager.
/// </summary>
public interface IStockCountClientService
{
    Task<IReadOnlyList<StockCountSessionDto>> ListAsync(
        StockCountStatus? status = null,
        CancellationToken cancellationToken = default);

    Task<StockCountSessionDto> CreateAsync(
        CreateStockCountRequestDto request,
        CancellationToken cancellationToken = default);

    Task<StockCountDetailDto> GetDetailAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<StockCountItemDto> ScanAsync(
        int id,
        string code,
        decimal quantityDelta = 1m,
        CancellationToken cancellationToken = default);

    Task<StockCountItemDto> SetCountedQuantityAsync(
        int id,
        int productId,
        decimal countedQuantity,
        CancellationToken cancellationToken = default);

    Task RemoveItemAsync(
        int id,
        int productId,
        CancellationToken cancellationToken = default);

    Task<StockCountReviewDto> GetReviewAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<StockCountSessionDto> CloseAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<StockCountSessionDto> ApplyAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<StockCountSessionDto> CancelAsync(
        int id,
        CancellationToken cancellationToken = default);
}
