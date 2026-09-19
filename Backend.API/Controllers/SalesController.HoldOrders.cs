using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using Sales.Module.DTOs;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class SalesController
{
    [HttpPost("{id}/hold")]
    public async Task<ActionResult<SaleDto>> HoldSaleAsync(int id, [FromBody] HoldSaleRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        string requestPath = $"/api/sales/{id}/hold";
        string bodyJson = GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);

        var resolved = await ResolveIdempotencyAsync(requestPath, bodyJson);
        if (resolved.ShouldStop) return resolved.BlockingResult!;

        try
        {
            var sale = await _salesService.HoldSaleAsync(id, request, resolved.Key, resolved.PayloadHash, GetActorUserId(), cancellationToken);
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
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> HoldSale(int id, [FromBody] HoldSaleRequestDto request) => HoldSaleAsync(id, request);

    [HttpPut("{id}/items")]
    public async Task<ActionResult<SaleDto>> UpdateSaleItemsAsync(int id, [FromBody] UpdateSaleItemsRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await IsAuthorizedForSaleAsync(id))
            {
                return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
            }

            bool isAuthorized = User.IsInRole("Admin") || User.IsInRole("Manager");
            var sale = await _salesService.UpdateSaleItemsAsync(id, request, isAuthorized, GetActorUserId(), cancellationToken);
            return Ok(sale);
        }
        catch (System.UnauthorizedAccessException ex)
        {
            return this.ApiForbidden(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> UpdateSaleItems(int id, [FromBody] UpdateSaleItemsRequestDto request) => UpdateSaleItemsAsync(id, request);

    [HttpPost("{id}/payments")]
    public async Task<ActionResult<SaleDto>> AddPaymentAsync(int id, [FromBody] AddPaymentRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        string requestPath = $"/api/sales/{id}/payments";
        string bodyJson = GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);

        var resolved = await ResolveIdempotencyAsync(requestPath, bodyJson);
        if (resolved.ShouldStop) return resolved.BlockingResult!;

        try
        {
            var sale = await _salesService.AddPaymentToHoldSaleAsync(id, request, resolved.Key, resolved.PayloadHash, GetActorUserId(), cancellationToken);
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
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> AddPayment(int id, [FromBody] AddPaymentRequestDto request) => AddPaymentAsync(id, request);

    [HttpPost("{id}/payments/batch")]
    public async Task<ActionResult<SaleDto>> AddPaymentsBatchAsync(int id, [FromBody] System.Collections.Generic.List<AddPaymentRequestDto> request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        string requestPath = $"/api/sales/{id}/payments/batch";
        string bodyJson = GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);

        var resolved = await ResolveIdempotencyAsync(requestPath, bodyJson);
        if (resolved.ShouldStop) return resolved.BlockingResult!;

        try
        {
            var sale = await _salesService.AddPaymentsBatchToHoldSaleAsync(id, request, resolved.Key, resolved.PayloadHash, GetActorUserId(), cancellationToken);
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
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> AddPaymentsBatch(int id, [FromBody] System.Collections.Generic.List<AddPaymentRequestDto> request) => AddPaymentsBatchAsync(id, request);

    [HttpGet("pending")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<System.Collections.Generic.IEnumerable<SaleDto>>> GetPendingSalesAsync([FromQuery] int limit = 200, [FromQuery] int offset = 0, CancellationToken cancellationToken = default)
    {
        limit = System.Math.Clamp(limit, 1, 1000);
        var pending = await _salesService.GetPendingSalesAsync(null, limit, offset, cancellationToken);

        var totalCount = await _salesService.CountPendingSalesAsync(null, cancellationToken);
        Response.Headers.Append("X-Total-Count", totalCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Ok(pending);
    }

    [NonAction]
    public Task<ActionResult<System.Collections.Generic.IEnumerable<SaleDto>>> GetPendingSales([FromQuery] int limit = 200, [FromQuery] int offset = 0) => GetPendingSalesAsync(limit, offset);

    [HttpPost("{id}/cancel")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<IActionResult> CancelSaleAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para anular esta venta.");
        }

        await _salesService.CancelSaleAsync(id, GetActorUserId(), cancellationToken);
        return Ok(new { message = $"Pedido #{id} anulado exitosamente." });
    }

    [NonAction]
    public Task<IActionResult> CancelSale(int id) => CancelSaleAsync(id);
}