using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.Helpers;
using Core.Logging;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using System.Threading.Tasks;
using Backend.API.Attributes;

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
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para consultar esta venta.");
        }

        var sale = await _salesService.GetSaleAsync(id, cancellationToken);
        if (sale == null) return this.ApiNotFound($"Venta #{id} no encontrada.");

        decimal rate = request.ExchangeRate > 0 ? request.ExchangeRate : sale.AppliedRate;
        if (rate <= 0) return this.ApiBadRequest("Tasa de cambio inválida.");

        decimal totalUsd = sale.TotalUSD;
        decimal totalBsS = PricingCalculator.RoundToDigital(totalUsd * rate);

        decimal totalPaidUsd = 0m;
        decimal totalPaidBsS = 0m;

        if (request.Payments != null)
        {
            foreach (var p in request.Payments)
            {
                decimal pUsd = p.Amount;
                decimal pBsS = p.AmountBsS > 0 ? p.AmountBsS : p.AmountLocal;

                if (pUsd <= 0 && pBsS > 0 && rate > 0)
                {
                    pUsd = PricingCalculator.ToUSD(pBsS, rate, decimals: 4);
                }
                else if (pBsS <= 0 && pUsd > 0 && rate > 0)
                {
                    pBsS = PricingCalculator.ToBsS(pUsd, rate);
                }

                totalPaidUsd += PricingCalculator.RoundToDigital(pUsd);
                totalPaidBsS += PricingCalculator.RoundToDigital(pBsS);
            }
        }

        decimal remainingUsd = Math.Max(0m, totalUsd - totalPaidUsd);
        decimal remainingBsS = Math.Max(0m, totalBsS - totalPaidBsS);

        bool isFullyPaid = remainingUsd <= 0.05m;
        decimal roundingAdjustment = remainingUsd <= 0.01m ? PricingCalculator.RoundToDigital(totalPaidBsS - totalBsS) : 0m;

        decimal changeUsd = 0m;
        decimal changeBsS = 0m;
        if (totalPaidUsd > totalUsd + 0.05m)
        {
            changeUsd = PricingCalculator.RoundToDigital(totalPaidUsd - totalUsd);
            changeBsS = PricingCalculator.ToBsS(changeUsd, rate);
        }

        return Ok(new CheckoutPreviewResponse
        {
            TotalUSD = totalUsd,
            TotalBsS = totalBsS,
            TotalPaidUSD = totalPaidUsd,
            TotalPaidBsS = totalPaidBsS,
            RemainingBalanceUSD = remainingUsd,
            RemainingBalanceBsS = remainingBsS,
            RoundingAdjustment = roundingAdjustment,
            ChangeDueUSD = changeUsd,
            ChangeDueBsS = changeBsS,
            IsFullyPaid = isFullyPaid
        });
    }

    [NonAction]
    public Task<ActionResult<CheckoutPreviewResponse>> GetCheckoutPreview(int id, [FromBody] CheckoutPreviewRequest request) => GetCheckoutPreviewAsync(id, request);

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

        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para completar esta venta.");
        }

        string requestPath = $"/api/sales/{id}/complete";
        string? idempotencyKey = Request?.Headers["Idempotency-Key"].ToString();

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ApiBadRequest("El encabezado Idempotency-Key es obligatorio para completar una venta.");
        }

        idempotencyKey = idempotencyKey.Trim();
        byte[]? payloadHash = null;

        if (_idempotencyService != null)
        {
            if (!_idempotencyService.ValidateKeyFormat(idempotencyKey, out var formatError))
            {
                return this.ApiBadRequest(formatError);
            }

            var bodyJson = GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(bodyJson);
            payloadHash = _idempotencyService.ComputePayloadHash(Request?.Method ?? "POST", requestPath, bodyBytes);

            var checkResult = await _idempotencyService.CheckAsync(idempotencyKey, requestPath, payloadHash, GetActorUserId(), HttpContext?.RequestAborted ?? default);
            if (checkResult.IsReplay)
            {
                if (Response?.Headers != null)
                {
                    Response.Headers["X-Cache-Lookup"] = "HIT";
                }

                if (int.TryParse(checkResult.StoredResponseBody, out int cachedInvoice))
                {
                    return Ok(cachedInvoice);
                }
                return Content(checkResult.StoredResponseBody ?? "", "application/json");
            }

            if (checkResult.IsMismatch)
            {
                var clientIp = HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
                AppLogger.LogSecurityAudit($"[IDEMPOTENCY_MISMATCH] Key={idempotencyKey}, Path={requestPath}, IP={clientIp}, Timestamp={System.DateTime.UtcNow:O}");
                return this.ApiUnprocessableEntity("La clave de idempotencia ya fue utilizada para una transacción diferente con otro contenido.");
            }
        }

        try
        {
            int? effectiveCashierId = _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid)
                ? uid
                : request.CashierId;

            var paymentInfos = request.Payments
                ?.Where(p => p != null)
                .Select(p => new PaymentInfo(p.PaymentMethodId, p.Amount, p.AmountBsS > 0 ? p.AmountBsS : p.AmountLocal, p.ReferenceNumber))
                ?? Enumerable.Empty<PaymentInfo>();
            int realId = await _salesService.CompleteSaleAsync(
                id, 
                request.ExchangeRate, 
                paymentInfos, 
                request.RoundingAdjustment, 
                effectiveCashierId, 
                request.IsPendingPickup, 
                idempotencyKey,
                payloadHash,
                HttpContext?.RequestAborted ?? default,
                GetActorUserId());

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            return Ok(realId);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.Message.Contains("IX_IdempotentRequests") || ex.InnerException?.Message.Contains("IX_IdempotentRequests") == true || (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505"))
        {
            if (_idempotencyService is Sales.Module.Services.IdempotencyService idService && !string.IsNullOrWhiteSpace(idempotencyKey) && payloadHash != null)
            {
                var collisionResult = await idService.HandleConcurrentCollisionAsync(idempotencyKey, requestPath, payloadHash, GetActorUserId(), HttpContext?.RequestAborted ?? default);
                if (collisionResult.IsReplay)
                {
                    if (Response?.Headers != null)
                    {
                        Response.Headers["X-Cache-Lookup"] = "HIT";
                    }

                    if (int.TryParse(collisionResult.StoredResponseBody, out int cachedInvoice))
                    {
                        return Ok(cachedInvoice);
                    }
                    return Content(collisionResult.StoredResponseBody ?? "", "application/json");
                }
                if (collisionResult.IsMismatch)
                {
                    var clientIp = HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
                    AppLogger.LogSecurityAudit($"[IDEMPOTENCY_MISMATCH] Key={idempotencyKey}, Path={requestPath}, IP={clientIp}, Timestamp={System.DateTime.UtcNow:O}");
                    return this.ApiUnprocessableEntity("La clave de idempotencia ya fue utilizada para una transacción diferente con otro contenido.");
                }
            }
            return this.ApiConflict("Operación concurrente en progreso para esta clave de idempotencia.");
        }
    }

    [NonAction]
    public Task<ActionResult> CompleteSale(int id, [FromBody] CompleteSaleRequest request) => CompleteSaleAsync(id, request);

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
            if (!await IsAuthorizedForSaleAsync(id))
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
    }

    [NonAction]
    public Task<ActionResult<SaleHistoryDto>> ConfirmPickup(int id) => ConfirmPickupAsync(id);

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

    [NonAction]
    public Task<ActionResult<System.Collections.Generic.IEnumerable<PendingPickupDto>>> GetPendingPickups([FromQuery] int limit = 200, [FromQuery] int offset = 0) => GetPendingPickupsAsync(limit, offset);
}