using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Backend.API.Attributes;
using Core.DTOs;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

/// <summary>
/// 8.187-T2 (F2a, Paso 11/12, S1/S2/S6): superficie HTTP aditiva del crédito CxC. El cierre a
/// crédito reutiliza el contrato del cierre regular (número de factura) y el perfil crediticio
/// expone cupo disponible, utilización, plazo/gracia y mora en tiempo real al POS.
/// </summary>
public partial class SalesController
{
    /// <summary>
    /// Cierra una venta a crédito (total o mixta): lo cobrado atraviesa el mismo camino del pago
    /// de contado (pago persistido + caja) y el remanente efectivo no cubierto se financia como
    /// Cuenta por Cobrar sin ingresar a la gaveta. Exige cliente registrado y elegible; los
    /// bloqueos de solvencia en español se resuelven en el middleware de dominio (400/409).
    /// </summary>
    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("{id}/complete-credit")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> CompleteCreditSaleAsync(int id, [FromBody] CreditSaleRequest request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para completar esta venta.");
        }

        // El usuario autenticado prevalece sobre el CashierId informado (mismo contrato del
        // cierre regular: la firma del historial no es delegable por el cuerpo de la petición).
        int? effectiveCashierId = _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid)
            ? uid
            : request.CashierId;
        request.CashierId = effectiveCashierId;

        int invoiceNumber = await _salesService.CompleteCreditSaleAsync(
            id,
            request,
            GetActorUserId(),
            HttpContext?.RequestAborted ?? default);

        if (Response?.Headers != null)
        {
            Response.Headers["X-Cache-Lookup"] = "MISS";
        }

        return Ok(invoiceNumber);
    }

    /// <summary>
    /// Perfil crediticio del cliente: cupo autorizado, saldo deudor de obligaciones abiertas,
    /// crédito disponible, porcentaje de utilización, plazo/gracia configurados y mora dinámica
    /// (vencimiento + días de gracia contra el día corriente de Venezuela). Cliente inexistente → 404.
    /// </summary>
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet("customers/{id}/credit-profile")]
    [ProducesResponseType(typeof(CreditProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CreditProfileDto>> GetCreditProfileAsync(
        int id,
        [FromServices] ICreditService creditService,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await creditService.GetCreditProfileAsync(id, cancellationToken);
            return Ok(profile);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    /// <summary>
    /// T3a (F2b, S3/S6): registra un abono CxC del cliente. La tasa la resuelve el servidor desde
    /// la fuente BCV cacheada (el contrato no expone tasa) y el abono se imputa FIFO de cartera o
    /// a las facturas explícitas informadas. El usuario autenticado prevalece como receptor del
    /// cobro; solo el monto cobrado en efectivo ingresa a la gaveta.
    /// </summary>
    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("credit-payments")]
    [ProducesResponseType(typeof(CreditPaymentResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CreditPaymentResultDto>> RegisterCreditPaymentAsync(
        [FromBody] CreditPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return this.ApiBadRequest("La solicitud del abono es requerida.");
        }

        // El usuario autenticado prevalece como receptor del cobro (la firma del historial no es
        // delegable por el cuerpo de la petición; mismo contrato del cierre de venta a crédito).
        int? actingUserId = GetActorUserId();
        request.ReceivedByUserId = actingUserId ?? request.ReceivedByUserId;

        try
        {
            var result = await _salesService.RegisterCreditPaymentAsync(
                request,
                actingUserId,
                HttpContext?.RequestAborted ?? default);

            return Ok(result);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    /// <summary>
    /// T3a (F2b, S3/S6): estado de cuenta dual del cliente — perfil crediticio, cartera completa
    /// con saldo USD y su equivalente Bs.S a la tasa del día, y el historial de abonos con el
    /// diferencial cambiario realizado. Cliente inexistente → 404.
    /// </summary>
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet("customers/{id}/credit-statement")]
    [ProducesResponseType(typeof(CreditStatementDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CreditStatementDto>> GetCreditStatementAsync(
        int id,
        [FromServices] ICreditService creditService,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var statement = await creditService.GetCreditStatementAsync(id, cancellationToken);
            return Ok(statement);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }
}
