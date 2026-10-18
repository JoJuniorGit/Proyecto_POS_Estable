using Backend.API.Attributes;
using Backend.API.Services;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

/// <summary>
/// T2a (Paso 13 / 8.188, S1/S4/S5): superficie HTTP de los egresos de caja chica del turno.
/// El registro y la anulación comparten el patrón de acciones protegidas: sin token (o con token
/// inválido/ajeno/ya usado/contexto distinto) responden 403 con authorizationAction=PettyCashExpense
/// y el contexto exacto; con un X-Authorization-Token vigente lo consumen (single-use) y reintentan.
/// </summary>
[Authorize]
[ApiController]
[Route("api/cashdrawer/petty-cash")]
public class PettyCashController : ControllerBase
{
    private const string AuthorizationTokenHeader = "X-Authorization-Token";

    private readonly IPettyCashService _pettyCashService;
    private readonly ICurrentUserService _currentUserService;
    // Opcional para preservar la construccion directa historica de tests; si falta, el gate de
    // autorizacion falla cerrado con el 403 del contrato.
    private readonly IAuthorizationCoordinator? _authorizationCoordinator;

    public PettyCashController(
        IPettyCashService pettyCashService,
        ICurrentUserService currentUserService,
        IAuthorizationCoordinator? authorizationCoordinator = null)
    {
        _pettyCashService = pettyCashService;
        _currentUserService = currentUserService;
        _authorizationCoordinator = authorizationCoordinator;
    }

    /// <summary>
    /// Registra el egreso de caja chica. La tasa BCV, la valoración bimonetaria y la liquidez por
    /// moneda las resuelve el servidor; el usuario autenticado prevalece como ejecutor.
    /// </summary>
    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost]
    [ProducesResponseType(typeof(PettyCashExpenseResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PettyCashExpenseResultDto>> RegisterAsync(
        [FromBody] PettyCashExpenseRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return this.ApiBadRequest("La solicitud del gasto de caja chica es requerida.");
        }

        // El usuario autenticado prevalece como ejecutor del gasto (la firma no es delegable por
        // el cuerpo de la petición; mismo contrato del cierre de venta y del abono CxC).
        int? actingUserId = GetActorUserId();
        UserRole? actingRole = _currentUserService.UserRole;

        try
        {
            var result = await _pettyCashService.RegisterAsync(
                request,
                actingUserId,
                actingRole,
                cancellationToken: cancellationToken);

            return Ok(result);
        }
        catch (PettyCashAuthorizationRequiredException signal)
        {
            return await RegisterWithAuthorizationAsync(request, signal, actingUserId, actingRole, cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiConflict(ex.Message);
        }
    }

    /// <summary>
    /// Anula el gasto de su sesión original abierta con motivo obligatorio. Genera la reversa
    /// compensatoria con los snapshots congelados del gasto (efecto neto 0 en la gaveta).
    /// </summary>
    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("{id:int}/cancel")]
    [ProducesResponseType(typeof(PettyCashExpenseResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PettyCashExpenseResultDto>> CancelAsync(
        int id,
        [FromBody] PettyCashCancelRequest? request,
        CancellationToken cancellationToken = default)
    {
        int? actingUserId = GetActorUserId();
        UserRole? actingRole = _currentUserService.UserRole;

        try
        {
            var result = await _pettyCashService.CancelAsync(
                id,
                request?.Reason,
                actingUserId,
                actingRole,
                cancellationToken: cancellationToken);

            return Ok(result);
        }
        catch (PettyCashAuthorizationRequiredException signal)
        {
            return await CancelWithAuthorizationAsync(id, request, signal, actingUserId, actingRole, cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiConflict(ex.Message);
        }
    }

    /// <summary>Gastos del turno (activos y anulados) para el listado de caja y supervisión.</summary>
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PettyCashExpenseResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<PettyCashExpenseResultDto>>> GetSessionExpensesAsync(
        [FromQuery] int sessionId,
        CancellationToken cancellationToken = default)
    {
        if (sessionId <= 0)
        {
            return this.ApiBadRequest("La sesión de caja es requerida para consultar los gastos de caja chica.");
        }

        return Ok(await _pettyCashService.GetSessionExpensesAsync(sessionId, cancellationToken));
    }

    /// <summary>
    /// Reintento del registro tras consumir el token del contexto exacto. Un token consumido sin
    /// efecto (contexto cambiado entre intentos) no registra nada: la caja debe generar una
    /// solicitud nueva, igual que el gate de precio manual.
    /// </summary>
    private async Task<ActionResult> RegisterWithAuthorizationAsync(
        PettyCashExpenseRequest request,
        PettyCashAuthorizationRequiredException signal,
        int? actingUserId,
        UserRole? actingRole,
        CancellationToken cancellationToken)
    {
        string? authorizationToken = ReadAuthorizationToken();

        if (!string.IsNullOrWhiteSpace(authorizationToken) && _authorizationCoordinator is not null && actingUserId is not null)
        {
            var consume = await _authorizationCoordinator.ConsumePettyCashExpenseAsync(
                authorizationToken,
                actingUserId,
                BuildContext(signal),
                cancellationToken);

            if (consume.Status == AuthorizationConsumeStatus.Consumed)
            {
                try
                {
                    var result = await _pettyCashService.RegisterAsync(
                        request,
                        actingUserId,
                        actingRole,
                        authorizedByUserId: consume.AuthorizedByUserId,
                        authorizationRequestId: consume.RequestId,
                        cancellationToken: cancellationToken);

                    return Ok(result);
                }
                catch (PettyCashAuthorizationRequiredException)
                {
                    // El token ya quedo consumido y el gasto no se registro (contexto cambiado o
                    // autorizacion presentada invalida): se responde el 403 del contrato.
                }
                catch (KeyNotFoundException ex)
                {
                    return this.ApiNotFound(ex.Message);
                }
                catch (ArgumentException ex)
                {
                    return this.ApiBadRequest(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return this.ApiConflict(ex.Message);
                }
            }
        }

        return this.ApiForbidden(
            $"{signal.Message} Se requiere autorización de un Administrador o Supervisor.",
            BuildForbiddenExtensions(signal));
    }

    /// <summary>Reintento de la anulación tras consumir el token del contexto exacto del gasto.</summary>
    private async Task<ActionResult> CancelWithAuthorizationAsync(
        int expenseId,
        PettyCashCancelRequest? request,
        PettyCashAuthorizationRequiredException signal,
        int? actingUserId,
        UserRole? actingRole,
        CancellationToken cancellationToken)
    {
        string? authorizationToken = ReadAuthorizationToken();

        if (!string.IsNullOrWhiteSpace(authorizationToken) && _authorizationCoordinator is not null && actingUserId is not null)
        {
            var consume = await _authorizationCoordinator.ConsumePettyCashExpenseAsync(
                authorizationToken,
                actingUserId,
                BuildContext(signal),
                cancellationToken);

            if (consume.Status == AuthorizationConsumeStatus.Consumed)
            {
                try
                {
                    var result = await _pettyCashService.CancelAsync(
                        expenseId,
                        request?.Reason,
                        actingUserId,
                        actingRole,
                        consume.AuthorizedByUserId,
                        cancellationToken);

                    return Ok(result);
                }
                catch (PettyCashAuthorizationRequiredException)
                {
                    // Token consumido sin efecto: se responde el 403 del contrato.
                }
                catch (KeyNotFoundException ex)
                {
                    return this.ApiNotFound(ex.Message);
                }
                catch (ArgumentException ex)
                {
                    return this.ApiBadRequest(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return this.ApiConflict(ex.Message);
                }
            }
        }

        return this.ApiForbidden(
            $"{signal.Message} Se requiere autorización de un Administrador o Supervisor.",
            BuildForbiddenExtensions(signal));
    }

    private static PettyCashExpenseAuthorizationContext BuildContext(PettyCashAuthorizationRequiredException signal) => new()
    {
        SessionId = signal.SessionId,
        Currency = (PettyCashCurrency)signal.Currency,
        AmountBsS = signal.AmountBsS,
        AmountUsd = signal.AmountUsd,
        Category = (PettyCashCategory)signal.Category
    };

    /// <summary>
    /// Contexto exacto del 403: el hub debe reenviarlo (sessionId, currency, amountBss, amountUsd,
    /// category) para que la solicitud quede ligada al mismo hash del token. Los enums viajan como
    /// enteros (convención del repo: sin JsonStringEnumConverter).
    /// </summary>
    private static IReadOnlyDictionary<string, object?> BuildForbiddenExtensions(PettyCashAuthorizationRequiredException signal) =>
        new Dictionary<string, object?>
        {
            ["authorizationRequired"] = true,
            ["authorizationAction"] = "PettyCashExpense",
            ["authorizationContext"] = new Dictionary<string, object?>
            {
                ["sessionId"] = signal.SessionId,
                ["currency"] = signal.Currency,
                ["amountBss"] = signal.AmountBsS,
                ["amountUsd"] = signal.AmountUsd,
                ["category"] = signal.Category
            }
        };

    private string? ReadAuthorizationToken()
        => Request.Headers.TryGetValue(AuthorizationTokenHeader, out var headerValues)
            ? headerValues.ToString()
            : null;

    private int? GetActorUserId()
        => _currentUserService.UserId is not null && int.TryParse(_currentUserService.UserId, out int userId)
            ? userId
            : null;
}
