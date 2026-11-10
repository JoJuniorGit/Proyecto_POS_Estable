using Sales.Module.DTOs.Profitability;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

/// <summary>
/// Paso 14 (F2): Estado de Resultados de rentabilidad. El resumen combina el bloque devengado,
/// el percibido en caja y sus desgloses, siempre sobre una ventana UTC estricta.
/// </summary>
public interface IProfitabilityService
{
    Task<ProfitabilitySummaryDto> GetSummaryAsync(
        ProfitabilityFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Paso 14 (F3-S1): desgloses del período (series, métodos, productos/categorías, vendedores
    /// y alertas) sin duplicar la lógica del resumen.
    /// </summary>
    Task<ProfitabilityBreakdownDto> GetBreakdownAsync(
        ProfitabilityFilterDto filter,
        CancellationToken cancellationToken = default);
}
