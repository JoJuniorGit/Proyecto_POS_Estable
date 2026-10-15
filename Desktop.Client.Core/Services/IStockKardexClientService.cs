using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;

namespace Desktop.Client.Services;

/// <summary>
/// 8.177-T3 (S2/S3): cliente Desktop del motor de consulta del Kardex de Inventario
/// (movimientos unificados, KPIs y exportación con encabezado legal).
/// </summary>
public interface IStockKardexClientService
{
    /// <summary>Página server-side de movimientos bajo los filtros combinables.</summary>
    Task<PagedResultDto<StockKardexRowDto>> GetMovementsAsync(
        StockKardexFilterDto filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>KPIs bajo los mismos filtros del listado.</summary>
    Task<StockKardexSummaryDto> GetSummaryAsync(
        StockKardexFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>Exporta todas las filas filtradas. Formatos válidos: csv, xlsx, pdf.</summary>
    Task<byte[]> ExportAsync(
        StockKardexFilterDto filter,
        string format,
        CancellationToken cancellationToken = default);
}
