using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using Sales.Module.DTOs;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class SalesController
{
    [HttpPost("{id}/hold")]
    public async Task<ActionResult<SaleDto>> HoldSale(int id, [FromBody] HoldSaleRequestDto request)
    {
        string requestPath = $"/api/sales/{id}/hold";
        string bodyJson = System.Text.Json.JsonSerializer.Serialize(request);

        var resolved = await ResolveIdempotencyAsync(requestPath, bodyJson);
        if (resolved.ShouldStop) return resolved.BlockingResult!;

        try
        {
            if (!await IsAuthorizedForSaleAsync(id))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para modificar esta venta." });
            }

            var sale = await _salesService.HoldSaleAsync(id, request, resolved.Key, resolved.PayloadHash);
            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }
            return Ok(sale);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.Message.Contains("IX_IdempotentRequests") || ex.InnerException?.Message.Contains("IX_IdempotentRequests") == true || (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505"))
        {
            return await HandleIdempotencyCollisionAsync(ex, requestPath, resolved.Key, resolved.PayloadHash);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
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

    [HttpPut("{id}/items")]
    public async Task<ActionResult<SaleDto>> UpdateSaleItems(int id, [FromBody] UpdateSaleItemsRequestDto request)
    {
        try
        {
            if (!await IsAuthorizedForSaleAsync(id))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para modificar esta venta." });
            }

            bool isAuthorized = User.IsInRole("Admin") || User.IsInRole("Manager");
            var sale = await _salesService.UpdateSaleItemsAsync(id, request, isAuthorized);
            return Ok(sale);
        }
        catch (System.UnauthorizedAccessException ex)
        {
            return this.ApiForbidden(ex.Message);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
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

    [HttpPost("{id}/payments")]
    public async Task<ActionResult<SaleDto>> AddPayment(int id, [FromBody] AddPaymentRequestDto request)
    {
        string requestPath = $"/api/sales/{id}/payments";
        string bodyJson = System.Text.Json.JsonSerializer.Serialize(request);

        var resolved = await ResolveIdempotencyAsync(requestPath, bodyJson);
        if (resolved.ShouldStop) return resolved.BlockingResult!;

        try
        {
            if (!await IsAuthorizedForSaleAsync(id))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para modificar esta venta." });
            }

            var sale = await _salesService.AddPaymentToHoldSaleAsync(id, request, resolved.Key, resolved.PayloadHash);
            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }
            return Ok(sale);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.Message.Contains("IX_IdempotentRequests") || ex.InnerException?.Message.Contains("IX_IdempotentRequests") == true || (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505"))
        {
            return await HandleIdempotencyCollisionAsync(ex, requestPath, resolved.Key, resolved.PayloadHash);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
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

    // 8.29-A05: abonos atómicos por lote (todo o nada) en una venta en espera. Un único
    // Idempotency-Key protege el lote completo: un replay no duplica NINGÚN abono.
    [HttpPost("{id}/payments/batch")]
    public async Task<ActionResult<SaleDto>> AddPaymentsBatch(int id, [FromBody] System.Collections.Generic.List<AddPaymentRequestDto> request)
    {
        string requestPath = $"/api/sales/{id}/payments/batch";
        string bodyJson = System.Text.Json.JsonSerializer.Serialize(request);

        var resolved = await ResolveIdempotencyAsync(requestPath, bodyJson);
        if (resolved.ShouldStop) return resolved.BlockingResult!;

        try
        {
            if (!await IsAuthorizedForSaleAsync(id))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para modificar esta venta." });
            }

            var sale = await _salesService.AddPaymentsBatchToHoldSaleAsync(id, request, resolved.Key, resolved.PayloadHash);
            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }
            return Ok(sale);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.Message.Contains("IX_IdempotentRequests") || ex.InnerException?.Message.Contains("IX_IdempotentRequests") == true || (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505"))
        {
            return await HandleIdempotencyCollisionAsync(ex, requestPath, resolved.Key, resolved.PayloadHash);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
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

    [HttpGet("pending")]
    public async Task<ActionResult<System.Collections.Generic.IEnumerable<SaleDto>>> GetPendingSales([FromQuery] int limit = 200, [FromQuery] int offset = 0)
    {
        // 8.2-M9: tope de cola acotado (max 1000) para no devolver el conjunto completo.
        limit = System.Math.Clamp(limit, 1, 1000);
        var (scopeToCashier, cashierId) = GetCashierReadScope();
        var pending = await _salesService.GetPendingSalesAsync(scopeToCashier ? cashierId : null, limit, offset);

        // 8.14-N1: total de la cola en cabecera para paginacion de UI sin romper el shape.
        var totalCount = await _salesService.CountPendingSalesAsync(scopeToCashier ? cashierId : null);
        Response.Headers.Append("X-Total-Count", totalCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Ok(pending);
    }

    [HttpPost("{id}/cancel")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<IActionResult> CancelSale(int id)
    {
        try
        {
            if (!await IsAuthorizedForSaleAsync(id))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para anular esta venta." });
            }

            await _salesService.CancelSaleAsync(id);
            return Ok(new { message = $"Pedido #{id} anulado exitosamente." });
        }
        catch (System.InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
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
}