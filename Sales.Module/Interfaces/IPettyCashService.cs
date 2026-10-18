using Core.Entities;
using Sales.Module.DTOs;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

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
    /// los snapshots exactos del gasto.
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
}
