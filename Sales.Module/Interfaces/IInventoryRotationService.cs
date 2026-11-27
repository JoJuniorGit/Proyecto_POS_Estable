using Core.DTOs;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

/// <summary>
/// Paso 15: reporte de rotacion y stock muerto. Se calcula on-demand sobre las ventas emitidas y
/// el catalogo actual; no persiste snapshots ni modifica precios.
/// </summary>
public interface IInventoryRotationService
{
    Task<InventoryRotationReportDto> GetReportAsync(
        InventoryRotationFilterDto filter,
        CancellationToken cancellationToken = default);

    Task<DeadStockSelectionDto> GetDeadStockProductIdsAsync(
        InventoryRotationFilterDto filter,
        CancellationToken cancellationToken = default);
}
