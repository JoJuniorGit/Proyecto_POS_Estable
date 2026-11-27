using System;
using System.Collections.Generic;
using Sales.Module.Entities;

namespace Sales.Module.DTOs;

/// <summary>
/// T2b (Paso 13 / 8.188, S2): renglón OPEX informativo del cierre Z. Totaliza SOLO los gastos de
/// caja chica <see cref="PettyCashStatus.Active"/> de la ventana del cierre, desglosados por
/// categoría y con su contravalor bimonetario congelado. Es un bloque de reporte: no vuelve a
/// restarse del esperado (esa deducción ya ocurrió una sola vez en el cálculo por método).
/// </summary>
public sealed record PettyCashCategorySummaryDto(
    PettyCashCategory Category,
    int Count,
    decimal TotalBsS,
    decimal TotalUsd);

/// <summary>
/// T2b (S2): resumen OPEX del cierre. <see cref="ActiveCount"/> y los totales excluyen los gastos
/// anulados; el par Expense/Reversal de una anulación netea cero en gaveta y no se reporta aquí.
/// </summary>
public sealed record PettyCashSummaryDto(
    int ActiveCount,
    decimal TotalBsS,
    decimal TotalUsd,
    IReadOnlyList<PettyCashCategorySummaryDto> ByCategory)
{
    public static PettyCashSummaryDto Empty { get; } = new(
        ActiveCount: 0,
        TotalBsS: 0m,
        TotalUsd: 0m,
        ByCategory: Array.Empty<PettyCashCategorySummaryDto>());
}
