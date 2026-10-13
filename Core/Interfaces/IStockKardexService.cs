using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;

namespace Core.Interfaces;

/// <summary>
/// 8.177-T2 (S2/S3): motor de consulta, KPIs y exportación del Kardex de Inventario.
/// Consulta la UNIÓN de movimientos activos + archivados (decisión L2 del mantenedor).
/// </summary>
public interface IStockKardexService
{
    /// <summary>
    /// Página server-side de movimientos bajo los filtros combinables. Clampea page ≥ 1 y
    /// pageSize 1..200 (default 25). Orden estable: MovementDate DESC, Source, MovementId DESC.
    /// </summary>
    Task<PagedResultDto<StockKardexRowDto>> GetMovementsAsync(
        StockKardexFilterDto filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>KPIs (count, volumen neto, entradas/mermas/neto USD y filas sin costo) bajo los mismos filtros.</summary>
    Task<StockKardexSummaryDto> GetSummaryAsync(
        StockKardexFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exporta TODAS las filas filtradas (sin paginar) con encabezado legal. Formatos: csv, xlsx, pdf.
    /// Un formato desconocido lanza <see cref="System.ArgumentException"/>.
    /// </summary>
    Task<byte[]> ExportAsync(
        StockKardexFilterDto filter,
        string format,
        CancellationToken cancellationToken = default);
}
