using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;

namespace Sales.Module.Interfaces;

public sealed record ReceivablesExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// Exportacion server-side del historial realizado de CxC y de la constancia de cancelacion.
/// </summary>
public interface IReceivablesExportService
{
    Task<ReceivablesExportFile> ExportPaidAsync(
        PaidReceivablesFilterDto filter,
        string mode,
        string format,
        CancellationToken cancellationToken = default);

    Task<ReceivablesExportFile> ExportStatementAsync(
        int obligationId,
        CancellationToken cancellationToken = default);
}
