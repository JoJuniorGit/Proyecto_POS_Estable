using Core.DTOs;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

/// <summary>
/// Paso 15: cliente WPF del reporte de rotacion y del puente de stock muerto al Paso 9.
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
