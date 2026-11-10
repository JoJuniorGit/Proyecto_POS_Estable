using Sales.Module.DTOs.Profitability;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

/// <summary>
/// Paso 14 (F3-S2): exportación server-side del Estado de Resultados en CSV y XLSX, reutilizable
/// por la API, el cliente WPF y el cliente Web. El PDF queda para F3-WU3.
/// </summary>
public interface IProfitabilityExportService
{
    Task<ProfitabilityExportFile> ExportAsync(
        ProfitabilityFilterDto filter,
        string format,
        ProfitabilityExportContext context,
        CancellationToken cancellationToken = default);
}
