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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckoutPreviewResponse>> GetCheckoutPreview(int id, [FromBody] CheckoutPreviewRequest request)
    {
        // 8.6-B1/8.5-A3: ownership a nivel de objeto — no se expone el desglose de una venta ajena.
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para consultar esta venta." });
        }

        var sale = await _salesService.GetSaleAsync(id);
        if (sale == null) return NotFound(new { message = $"Venta #{id} no encontrada." });

        decimal rate = request.ExchangeRate > 0 ? request.ExchangeRate : sale.AppliedRate;
        if (rate <= 0) return BadRequest(new { message = "Tasa de cambio inválida." });

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
                    // 8C-M1: conversión de alta precisión (4 decimales) para no perder centésimas
                    // en el redondeo intermedio; el total final se redondea a 2 (RoundToDigital).
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

    [RequireSecurityStampValidation]
    [HttpPost("{id}/complete")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> CompleteSale(int id, [FromBody] CompleteSaleRequest request)
    {
        if (User.IsInRole("Driver"))
        {
            return Forbid();
        }

        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para completar esta venta." });
        }

        string requestPath = $"/api/sales/{id}/complete";
        string? idempotencyKey = Request?.Headers["Idempotency-Key"].ToString();

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return BadRequest(new { message = "El encabezado Idempotency-Key es obligatorio para completar una venta." });
        }

        idempotencyKey = idempotencyKey.Trim();
        byte[]? payloadHash = null;

        if (_idempotencyService != null)
        {
            if (!_idempotencyService.ValidateKeyFormat(idempotencyKey, out var formatError))
            {
                return BadRequest(new { message = formatError });
            }

            var bodyJson = System.Text.Json.JsonSerializer.Serialize(request);
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(bodyJson);
            payloadHash = _idempotencyService.ComputePayloadHash(Request?.Method ?? "POST", requestPath, bodyBytes);

            var checkResult = await _idempotencyService.CheckAsync(idempotencyKey, requestPath, payloadHash, HttpContext?.RequestAborted ?? default);
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
                return StatusCode(StatusCodes.Status422UnprocessableEntity, new
                {
                    message = "La clave de idempotencia ya fue utilizada para una transacción diferente con otro contenido."
                });
            }
        }

        try
        {
            int? effectiveCashierId = _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid)
                ? uid
                : request.CashierId;

            var paymentInfos = request.Payments.Select(p => new PaymentInfo(p.PaymentMethodId, p.Amount, p.AmountBsS > 0 ? p.AmountBsS : p.AmountLocal, p.ReferenceNumber));
            int realId = await _salesService.CompleteSaleAsync(
                id, 
                request.ExchangeRate, 
                paymentInfos, 
                request.RoundingAdjustment, 
                effectiveCashierId, 
                request.IsPendingPickup, 
                idempotencyKey,
                payloadHash,
                HttpContext?.RequestAborted ?? default);

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            return Ok(realId);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.Message.Contains("IX_IdempotentRequests") || ex.InnerException?.Message.Contains("IX_IdempotentRequests") == true || (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505"))
        {
            // Manejo de colisión concurrente: reintentar lectura
            if (_idempotencyService is Sales.Module.Services.IdempotencyService idService && !string.IsNullOrWhiteSpace(idempotencyKey) && payloadHash != null)
            {
                var collisionResult = await idService.HandleConcurrentCollisionAsync(idempotencyKey, requestPath, payloadHash, HttpContext?.RequestAborted ?? default);
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
                    return StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = "La clave de idempotencia ya fue utilizada para una transacción diferente con otro contenido." });
                }
            }
            return StatusCode(StatusCodes.Status409Conflict, new { message = "Operación concurrente en progreso para esta clave de idempotencia." });
        }
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
    public async Task<ActionResult<SaleHistoryDto>> ConfirmPickup(int id)
    {
        try
        {
            if (!await IsAuthorizedForSaleAsync(id))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para confirmar esta entrega." });
            }

            var sale = await _salesService.ConfirmPickupAsync(id);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
        catch (System.ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (System.InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [HttpGet("pending-pickups")]
    public async Task<ActionResult<System.Collections.Generic.IEnumerable<PendingPickupDto>>> GetPendingPickups([FromQuery] int limit = 200, [FromQuery] int offset = 0)
    {
        // 8.2-M9: tope de cola acotado (max 1000).
        limit = System.Math.Clamp(limit, 1, 1000);
        var (scopeToCashier, cashierId) = GetCashierReadScope();
        var pending = await _salesService.GetPendingPickupsAsync(scopeToCashier ? cashierId : null, limit, offset);

        // 8.14-N1: total de la cola en cabecera para paginacion de UI sin romper el shape.
        var totalCount = await _salesService.CountPendingPickupsAsync(scopeToCashier ? cashierId : null);
        Response.Headers.Append("X-Total-Count", totalCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Ok(pending);
    }
}