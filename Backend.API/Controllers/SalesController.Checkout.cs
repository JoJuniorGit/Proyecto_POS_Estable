using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.Logging;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using System.Threading.Tasks;
using Backend.API.Attributes;
using Backend.API.Services;

namespace Backend.API.Controllers;

public partial class SalesController
{
    [HttpPost("{id}/checkout-preview")]
    [ProducesResponseType(typeof(CheckoutPreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckoutPreviewResponse>> GetCheckoutPreviewAsync(int id, [FromBody] CheckoutPreviewRequest request, System.Threading.CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para consultar esta venta.");
        }

        // 8.159-T1 (CLEAN-04, REQ-CHC-01): el cálculo financiero vive en el servicio de dominio;
        // el endpoint solo autoriza y delega (missing → 404 y tasa inválida → 400 los mapea el
        // middleware de dominio con los mismos mensajes del comportamiento histórico).
        var preview = await _checkoutCalculator!.CalculatePreviewAsync(id, request, cancellationToken);
        return Ok(preview);
    }

    [RequireSecurityStampValidation]
    [HttpPost("{id}/complete")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> CompleteSaleAsync(int id, [FromBody] CompleteSaleRequest request, System.Threading.CancellationToken cancellationToken = default)
    {
        if (User.IsInRole("Driver"))
        {
            return this.ApiForbidden("El rol Driver no tiene permisos para completar ventas.");
        }

        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para completar esta venta.");
        }

        string requestPath = $"/api/sales/{id}/complete";
        var bodyJson = GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);
        var resolved = await _idempotencyResolver.ResolveAsync(
            this,
            requestPath,
            bodyJson,
            missingKeyMessage: "El encabezado Idempotency-Key es obligatorio para completar una venta.",
            parseNumericBodyAsInvoice: true);

        if (resolved.ShouldStop)
        {
            return resolved.BlockingResult!;
        }

        try
        {
            int realId = await _salesService.CompleteSaleAsync(
                id, 
                request.ExchangeRate, 
                MapPaymentInfos(request), 
                request.RoundingAdjustment, 
                ResolveEffectiveCashierId(request), 
                request.IsPendingPickup, 
                resolved.Key,
                resolved.PayloadHash,
                HttpContext?.RequestAborted ?? default,
                GetActorUserId());

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            return Ok(realId);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IdempotencyRequestResolver.IsIdempotencyUniqueViolation(ex))
        {
            return await _idempotencyResolver.HandleCollisionAsync(this, ex, requestPath, resolved.Key, resolved.PayloadHash, parseNumericBodyAsInvoice: true);
        }
    }

    private int? ResolveEffectiveCashierId(CompleteSaleRequest request)
    {
        return _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid)
            ? uid
            : request.CashierId;
    }

    private static System.Collections.Generic.IEnumerable<PaymentInfo> MapPaymentInfos(CompleteSaleRequest request)
    {
        return request.Payments
            ?.Where(p => p != null)
            .Select(p => new PaymentInfo(p.PaymentMethodId, p.Amount, p.AmountBsS > 0 ? p.AmountBsS : p.AmountLocal, p.ReferenceNumber))
            ?? System.Linq.Enumerable.Empty<PaymentInfo>();
    }

    [HttpGet("idempotency/stats")]
    [Authorize(Roles = "Admin")]
    public ActionResult GetIdempotencyStats()
    {
        if (_idempotencyService == null)
        {
            return Ok(new { Hits = 0, Misses = 0, Conflicts = 0 });
        }

        return Ok(new
        {
            Hits = _idempotencyService.Hits,
            Misses = _idempotencyService.Misses,
            Conflicts = _idempotencyService.Conflicts
        });
    }

    [HttpPost("{id}/confirm-pickup")]
    public async Task<ActionResult<SaleHistoryDto>> ConfirmPickupAsync(int id, System.Threading.CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
            {
                return this.ApiForbidden("Acceso denegado: no tiene permisos para confirmar esta entrega.");
            }

            var sale = await _salesService.ConfirmPickupAsync(id, GetActorUserId(), cancellationToken);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return this.ApiNotFound($"Venta con ID {id} no encontrada.");
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return this.ApiConflict("Otro usuario modificó el retiro simultáneamente. Actualice la lista e intente de nuevo.");
        }
    }

    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet("pending-pickups")]
    public async Task<ActionResult<System.Collections.Generic.IEnumerable<PendingPickupDto>>> GetPendingPickupsAsync([FromQuery] int limit = 200, [FromQuery] int offset = 0, System.Threading.CancellationToken cancellationToken = default)
    {
        limit = System.Math.Clamp(limit, 1, 1000);
        var (scopeToCashier, cashierId) = GetCashierReadScope();
        var pending = await _salesService.GetPendingPickupsAsync(scopeToCashier ? cashierId : null, limit, offset, cancellationToken);

        var totalCount = await _salesService.CountPendingPickupsAsync(scopeToCashier ? cashierId : null, cancellationToken);
        Response.Headers.Append("X-Total-Count", totalCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Ok(pending);
    }
}
