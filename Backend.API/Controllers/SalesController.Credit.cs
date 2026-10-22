using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Backend.API.Attributes;
using Backend.API.Services;
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
    /// T3b1 (S4): cuando el ÚNICO bloqueo es el sobregiro de cupo, responde 403 con
    /// authorizationRequired/authorizationAction=CreditOverLimit + contexto (mismo contrato del
    /// gate de precio manual); con un X-Authorization-Token vigente para el contexto exacto, lo
    /// consume (single-use) y reintenta habilitando SOLO la excepción de sobregiro.
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

        string requestPath = $"/api/sales/{id}/complete-credit";
        string bodyJson = GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);
        var resolved = await _idempotencyResolver.ResolveIfPresentAsync(
            this,
            requestPath,
            bodyJson,
            parseNumericBodyAsInvoice: true);

        if (resolved.ShouldStop)
        {
            return resolved.BlockingResult!;
        }

        try
        {
            return await CompleteCreditSaleCoreAsync(id, request, creditOverLimitAuthorized: false, resolved, requestPath, cancellationToken);
        }
        catch (CreditOverLimitRequiresAuthorizationException signal)
        {
            return await ResolveCreditOverLimitAsync(id, request, signal, resolved, requestPath, cancellationToken);
        }
    }

    /// <summary>
    /// T3b1 (S4): intento de cierre con la bandera de autorización resuelta. La ruta regular del
    /// endpoint siempre llega con false (el sobregiro debe señalarse); el reintento tras consumir
    /// el token llega con true y el servicio vuelve a validar el resto de los bloqueos.
    /// </summary>
    private async Task<ActionResult> CompleteCreditSaleCoreAsync(
        int saleId,
        CreditSaleRequest request,
        bool creditOverLimitAuthorized,
        IdempotencyResolution idempotency,
        string requestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            int invoiceNumber = await _salesService.CompleteCreditSaleAsync(
                saleId,
                request,
                GetActorUserId(),
                creditOverLimitAuthorized,
                cancellationToken);

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            await _idempotencyResolver.RegisterSuccessAsync(
                idempotency,
                requestPath,
                invoiceNumber.ToString(),
                cancellationToken);

            return Ok(invoiceNumber);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IdempotencyRequestResolver.IsIdempotencyUniqueViolation(ex))
        {
            return await _idempotencyResolver.HandleCollisionAsync(this, ex, requestPath, idempotency.Key, idempotency.PayloadHash, parseNumericBodyAsInvoice: true);
        }
    }

    /// <summary>
    /// T3b1 (S4): traduce la señal de sobregiro al canal de autorizaciones. Sin token (o con token
    /// inválido/ajeno/ya usado/contexto distinto) responde el 403 del contrato de acciones
    /// protegidas con el contexto exacto —los mismos valores que el hash liga al token—; con token
    /// vigente lo consume y reintenta el cierre con la única excepción habilitada. Los bloqueos no
    /// autorizables no llegan aquí: el servicio no emite la señal para ellos.
    /// </summary>
    private async Task<ActionResult> ResolveCreditOverLimitAsync(
        int saleId,
        CreditSaleRequest request,
        CreditOverLimitRequiresAuthorizationException signal,
        IdempotencyResolution idempotency,
        string requestPath,
        CancellationToken cancellationToken)
    {
        string? authorizationToken = Request.Headers.TryGetValue(AuthorizationTokenHeader, out var headerValues)
            ? headerValues.ToString()
            : null;

        if (!string.IsNullOrWhiteSpace(authorizationToken) && _authorizationCoordinator is not null)
        {
            var operation = new CreditOverLimitContext
            {
                CustomerId = signal.CustomerId,
                SaleId = signal.SaleId,
                FinancedUSD = signal.FinancedUSD,
                AvailableCreditUSD = signal.AvailableCreditUSD
            };

            var consume = await _authorizationCoordinator.ConsumeCreditOverLimitAsync(
                authorizationToken,
                saleId,
                GetActorUserId(),
                operation,
                cancellationToken);

            if (consume.Status == AuthorizationConsumeStatus.Consumed)
            {
                try
                {
                    return await CompleteCreditSaleCoreAsync(saleId, request, creditOverLimitAuthorized: true, idempotency, requestPath, cancellationToken);
                }
                catch (CreditOverLimitRequiresAuthorizationException)
                {
                    // El flag habilita únicamente el sobregiro: si el contexto cambió entre el
                    // intento y el reintento, el cierre no se completa (token ya consumido) y la
                    // cajera debe generar una solicitud nueva, igual que el gate de precio manual.
                }
            }
        }

        // Contexto exacto del 403: el hub debe reenviarlo (customerId, financedUSD,
        // availableCreditUSD) para que la solicitud quede ligada al mismo hash del token.
        var extensions = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["authorizationRequired"] = true,
            ["authorizationAction"] = "CreditOverLimit",
            ["authorizationContext"] = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["saleId"] = signal.SaleId,
                ["customerId"] = signal.CustomerId,
                ["customerName"] = signal.CustomerName,
                ["financedUSD"] = signal.FinancedUSD,
                ["availableCreditUSD"] = signal.AvailableCreditUSD
            }
        };

        return this.ApiForbidden($"{signal.Message} Se requiere autorización de un Administrador o Supervisor.", extensions);
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

        string requestPath = "/api/sales/credit-payments";
        string bodyJson = actingUserId + "|" + System.Text.Json.JsonSerializer.Serialize(request);
        var resolved = await _idempotencyResolver.ResolveIfPresentAsync(
            this,
            requestPath,
            bodyJson,
            parseNumericBodyAsInvoice: false);

        if (resolved.ShouldStop)
        {
            return (ActionResult<CreditPaymentResultDto>)resolved.BlockingResult!;
        }

        try
        {
            var result = await _salesService.RegisterCreditPaymentAsync(
                request,
                actingUserId,
                HttpContext?.RequestAborted ?? default);

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            await _idempotencyResolver.RegisterSuccessAsync(
                resolved,
                requestPath,
                System.Text.Json.JsonSerializer.Serialize(result),
                cancellationToken);

            return Ok(result);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IdempotencyRequestResolver.IsIdempotencyUniqueViolation(ex))
        {
            var collisionResult = await _idempotencyResolver.HandleCollisionAsync(this, ex, requestPath, resolved.Key, resolved.PayloadHash, parseNumericBodyAsInvoice: false);
            return (ActionResult<CreditPaymentResultDto>)collisionResult;
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

    /// <summary>
    /// T3b2 (F2b, S5/S7): anula una venta a crédito SIN abonos. Restituye stock y cupo, marca la
    /// obligación Anulada y la venta Cancelada sin generar movimientos de caja. Con abonos o
    /// devoluciones previas la anulación simple se bloquea (409). Mismo contrato de propiedad y
    /// sello de seguridad que el cierre a crédito.
    /// </summary>
    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("{id}/credit-cancel")]
    [ProducesResponseType(typeof(CreditCancelResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreditCancelResultDto>> CancelCreditSaleAsync(
        int id,
        [FromBody] CreditCancelRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para anular esta venta.");
        }

        int? actingUserId = GetActorUserId();
        string requestPath = $"/api/sales/{id}/credit-cancel";
        string bodyJson = actingUserId + "|" + System.Text.Json.JsonSerializer.Serialize(request);
        var resolved = await _idempotencyResolver.ResolveIfPresentAsync(
            this,
            requestPath,
            bodyJson,
            parseNumericBodyAsInvoice: false);

        if (resolved.ShouldStop)
        {
            return (ActionResult<CreditCancelResultDto>)resolved.BlockingResult!;
        }

        try
        {
            var result = await _salesService.CancelCreditSaleAsync(
                id,
                request?.Reason,
                actingUserId,
                HttpContext?.RequestAborted ?? cancellationToken);

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            await _idempotencyResolver.RegisterSuccessAsync(
                resolved,
                requestPath,
                System.Text.Json.JsonSerializer.Serialize(result),
                cancellationToken);

            return Ok(result);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IdempotencyRequestResolver.IsIdempotencyUniqueViolation(ex))
        {
            var collisionResult = await _idempotencyResolver.HandleCollisionAsync(this, ex, requestPath, resolved.Key, resolved.PayloadHash, parseNumericBodyAsInvoice: false);
            return (ActionResult<CreditCancelResultDto>)collisionResult;
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    /// <summary>
    /// T3b2 (F2b, S5/S7): registra la devolución total o parcial de una venta a crédito. El valor
    /// por línea se calcula server-side sobre la base efectiva; el overpago efectivamente pagado
    /// se reembolsa por caja a la tasa congelada del último abono. El usuario autenticado
    /// prevalece como autor de la devolución y la clave de idempotencia replica sin nuevos
    /// movimientos.
    /// </summary>
    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("{id}/credit-return")]
    [ProducesResponseType(typeof(CreditReturnResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreditReturnResultDto>> RegisterCreditReturnAsync(
        int id,
        [FromBody] CreditReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return this.ApiBadRequest("La solicitud de la devolución es requerida.");
        }

        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para registrar devoluciones de esta venta.");
        }

        int? actingUserId = GetActorUserId();
        string requestPath = $"/api/sales/{id}/credit-return";
        string bodyJson = actingUserId + "|" + System.Text.Json.JsonSerializer.Serialize(request);
        var resolved = await _idempotencyResolver.ResolveIfPresentAsync(
            this,
            requestPath,
            bodyJson,
            parseNumericBodyAsInvoice: false);

        if (resolved.ShouldStop)
        {
            return (ActionResult<CreditReturnResultDto>)resolved.BlockingResult!;
        }

        try
        {
            var result = await _salesService.RegisterCreditReturnAsync(
                id,
                request,
                actingUserId,
                HttpContext?.RequestAborted ?? cancellationToken);

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            await _idempotencyResolver.RegisterSuccessAsync(
                resolved,
                requestPath,
                System.Text.Json.JsonSerializer.Serialize(result),
                cancellationToken);

            return Ok(result);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IdempotencyRequestResolver.IsIdempotencyUniqueViolation(ex))
        {
            var collisionResult = await _idempotencyResolver.HandleCollisionAsync(this, ex, requestPath, resolved.Key, resolved.PayloadHash, parseNumericBodyAsInvoice: false);
            return (ActionResult<CreditReturnResultDto>)collisionResult;
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    /// <summary>
    /// T4-prep (S1/S4): escritura administrativa del perfil crediticio desde la UI. Solo
    /// Admin/Manager; el estado Suspendido por Mora es system-managed y la validación de cupo,
    /// plazo, gracia y notas responde 400. Cliente inexistente → 404.
    /// </summary>
    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager")]
    [HttpPut("customers/{id}/credit-profile")]
    [ProducesResponseType(typeof(CreditProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CreditProfileDto>> UpdateCreditProfileAsync(
        int id,
        [FromBody] CustomerCreditProfileUpdateDto request,
        [FromServices] ICreditService creditService,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return this.ApiBadRequest("La solicitud del perfil crediticio es requerida.");
        }

        try
        {
            var profile = await creditService.UpdateCustomerCreditProfileAsync(id, request, cancellationToken);
            return Ok(profile);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
        catch (System.ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    /// <summary>
    /// T4-prep (S3): cartera global de cuentas por cobrar con filtros por cliente, estado, mora
    /// dinámica y búsqueda, más los totales de cartera abierta/vencida. Lectura para caja y
    /// supervisión (Admin/Manager/Cashier). Un estado desconocido responde 400.
    /// </summary>
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet("credit-receivables")]
    [ProducesResponseType(typeof(CreditReceivablesListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreditReceivablesListDto>> GetCreditReceivablesAsync(
        [FromQuery] CreditReceivablesFilterDto filter,
        [FromServices] ICreditService creditService,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var receivables = await creditService.GetCreditReceivablesAsync(
                filter ?? new CreditReceivablesFilterDto(),
                cancellationToken);
            return Ok(receivables);
        }
        catch (System.ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }
}
