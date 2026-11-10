using Core.Entities;
using Sales.Module.DTOs;
using Sales.Module.Receipts;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

/// <summary>
/// T3 (Paso 13 / 8.188, S7): datos del vale de caja chica para el PDF on-demand, junto con el
/// creador del gasto para el ownership del endpoint (el cajero solo descarga sus propios vales;
/// Admin/Manager tienen bypass).
/// </summary>
public sealed record PettyCashVoucherData(PettyCashVoucherContext Context, int CreatedByUserId);

/// <summary>
/// T2a (Paso 13 / 8.188, S1/S3/S5): registro, anulación y consulta de los egresos de caja chica
/// del turno. El registro valida liquidez física de la moneda, congela la tasa BCV con la
/// valoración bimonetaria y crea el movimiento Expense en la misma transacción; la anulación es
/// lógica y compensa con un Income que copia exactamente los snapshots del gasto.
/// </summary>
public interface IPettyCashService
{
    /// <summary>
    /// Registra un egreso de caja chica. <paramref name="authorizedByUserId"/> y
    /// <paramref name="authorizationRequestId"/> solo se completan cuando el controlador consumió
    /// un token de autorización del contexto exacto; la jerarquía (Cashier siempre, Manager sobre
    /// el umbral, Admin directo) se resuelve con <c>PettyCashAuthorizationPolicy</c>.
    /// </summary>
    Task<PettyCashExpenseResultDto> RegisterAsync(
        PettyCashExpenseRequest request,
        int? actingUserId = null,
        UserRole? actingRole = null,
        int? authorizedByUserId = null,
        int? authorizationRequestId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Anula un gasto de su sesión original abierta con motivo obligatorio. Admin anula directo;
    /// Manager y Cashier requieren autorización supervisora (<paramref name="authorizedByUserId"/>
    /// llega del token consumido por el controlador). Genera la reversa compensatoria Income con
    /// los snapshots exactos del gasto. H3 (cierre 8.188): cuando el aprobador es distinto del
    /// ejecutor, queda vinculado en <c>PettyCashExpense.CancellationAuthorizedByUserId</c> y el
    /// vale imprime la línea "Autorizó anulación"; la anulación directa de Admin queda en null.
    /// </summary>
    Task<PettyCashExpenseResultDto> CancelAsync(
        int expenseId,
        string? reason,
        int? actingUserId = null,
        UserRole? actingRole = null,
        int? authorizedByUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Gastos del turno (activos y anulados) en orden de registro, con su movimiento vinculado.</summary>
    Task<IReadOnlyList<PettyCashExpenseResultDto>> GetSessionExpensesAsync(
        int sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// S7: carga el gasto con su sesión y resuelve los nombres de los usuarios (cajero,
    /// autorizador y anulador) para armar el vale VCC-000001. Gasto inexistente → null (el
    /// controlador responde 404). No hay cola de impresión: el PDF se genera on-demand.
    /// </summary>
    Task<PettyCashVoucherData?> GetVoucherDataAsync(
        int expenseId,
        CancellationToken cancellationToken = default);
}
