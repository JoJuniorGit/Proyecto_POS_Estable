using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using Core.Logging;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Attributes;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SalesController : ControllerBase
{
    private readonly ISalesService _salesService;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;
    private readonly Core.Interfaces.IIdempotencyService? _idempotencyService;

    public SalesController(
        ISalesService salesService, 
        Core.Interfaces.ICurrentUserService currentUserService,
        Core.Interfaces.IIdempotencyService? idempotencyService = null)
    {
        _salesService = salesService;
        _currentUserService = currentUserService;
        _idempotencyService = idempotencyService;
    }

    [HttpPost("start")]
    public async Task<ActionResult<SaleDto>> StartSale([FromQuery] int? cashierId = null)
    {
        int? effectiveCashierId = _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid) 
            ? uid 
            : cashierId;

        var _sale = await _salesService.StartSaleAsync(effectiveCashierId);
        return Ok(_sale);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<SaleDto>> GetSale(int id)
    {
        try
        {
            var _sale = await _salesService.GetSaleAsync(id);
            return Ok(_sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{id}/items")]
    public async Task<ActionResult<SaleDto>> AddItem(int id, [FromBody] AddItemRequest request)
    {
        bool isAuthorized = User.IsInRole("Admin") || User.IsInRole("Manager");
        if ((request.CustomUnitPriceUsd.HasValue || request.CustomUnitPriceLocal.HasValue) && !isAuthorized)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor." });
        }

        var _sale = await _salesService.AddItemAsync(id, request.ProductId, request.Quantity, request.ExchangeRate, request.CustomUnitPriceUsd, request.CustomUnitPriceLocal, isAuthorized);
        return Ok(_sale);
    }

    [HttpDelete("{id}/items/{itemId}")]
    public async Task<ActionResult<SaleDto>> RemoveItem(int id, int itemId, [FromQuery] decimal exchangeRate)
    {
        var _sale = await _salesService.RemoveItemAsync(id, itemId, exchangeRate);
        return Ok(_sale);
    }

    [HttpPut("{id}/items/{itemId}")]
    public async Task<ActionResult<SaleDto>> UpdateItemQuantity(int id, int itemId, [FromBody] UpdateQuantityRequest request)
    {
        var _sale = await _salesService.UpdateItemQuantityAsync(id, itemId, request.Quantity, request.ExchangeRate);
        return Ok(_sale);
    }

    [HttpPut("{id}/exchange-rate")]
    public async Task<ActionResult<SaleDto>> UpdateExchangeRate(int id, [FromQuery] decimal exchangeRate)
    {
        try
        {
            var _sale = await _salesService.UpdateExchangeRateAsync(id, exchangeRate);
            return Ok(_sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/hold")]
    public async Task<ActionResult<SaleDto>> HoldSale(int id, [FromBody] HoldSaleRequestDto request)
    {
        try
        {
            var _sale = await _salesService.HoldSaleAsync(id, request);
            return Ok(_sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}/items")]
    public async Task<ActionResult<SaleDto>> UpdateSaleItems(int id, [FromBody] UpdateSaleItemsRequestDto request)
    {
        try
        {
            bool isAuthorized = User.IsInRole("Admin") || User.IsInRole("Manager");
            var _sale = await _salesService.UpdateSaleItemsAsync(id, request, isAuthorized);
            return Ok(_sale);
        }
        catch (System.UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/payments")]
    public async Task<ActionResult<SaleDto>> AddPayment(int id, [FromBody] AddPaymentRequestDto request)
    {
        try
        {
            var _sale = await _salesService.AddPaymentToHoldSaleAsync(id, request);
            return Ok(_sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("pending")]
    public async Task<ActionResult<System.Collections.Generic.IEnumerable<SaleDto>>> GetPendingSales()
    {
        var _pending = await _salesService.GetPendingSalesAsync();
        return Ok(_pending);
    }

    [HttpPost("{id}/cancel")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<IActionResult> CancelSale(int id)
    {
        try
        {
            await _salesService.CancelSaleAsync(id);
            return Ok(new { message = $"Pedido #{id} anulado exitosamente." });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("customers")]
    public async Task<ActionResult> GetCustomers(
        [FromQuery] string? query = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool recentOnly = false)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (items, totalCount) = await _salesService.GetCustomersAsync(query, page, pageSize, recentOnly);
        Response.Headers["X-Total-Count"] = totalCount.ToString();
        return Ok(new { items, totalCount, page, pageSize });
    }


    [HttpGet("customers/default")]
    public async Task<ActionResult<CustomerDto>> GetDefaultCustomer()
    {
        try
        {
            var _customer = await _salesService.GetDefaultCustomerAsync();
            return Ok(_customer);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{id}/customer")]
    public async Task<ActionResult<SaleDto>> UpdateSaleCustomer(int id, [FromBody] UpdateSaleCustomerRequest request)
    {
        try
        {
            var _sale = await _salesService.UpdateSaleCustomerAsync(id, request.CustomerId);
            return Ok(_sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("customers")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<CustomerDto>> CreateCustomer([FromBody] CreateCustomerDto request)
    {
        try
        {
            var _customer = await _salesService.CreateCustomerAsync(request);
            return Ok(_customer);
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("customers/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<CustomerDto>> UpdateCustomer(int id, [FromBody] UpdateCustomerDto request)
    {
        try
        {
            var _customer = await _salesService.UpdateCustomerAsync(id, request);
            return Ok(_customer);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("customers/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult> DeleteCustomer(int id)
    {
        try
        {
            await _salesService.DeleteCustomerAsync(id);
            return NoContent();
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
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

            var _payment_infos = request.Payments.Select(p => new PaymentInfo(p.PaymentMethodId, p.Amount, p.AmountBsS > 0 ? p.AmountBsS : p.AmountLocal, p.ReferenceNumber));
            int _real_id = await _salesService.CompleteSaleAsync(
                id, 
                request.ExchangeRate, 
                _payment_infos, 
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

            return Ok(_real_id);
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
            var _sale = await _salesService.ConfirmPickupAsync(id);
            return Ok(_sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("pending-pickups")]
    public async Task<ActionResult<System.Collections.Generic.IEnumerable<PendingPickupDto>>> GetPendingPickups()
    {
        var _pending = await _salesService.GetPendingPickupsAsync();
        return Ok(_pending);
    }

    /// <summary>
    /// Obtiene el historial paginado de ventas completadas con filtros opcionales de fecha y término de búsqueda.
    /// </summary>
    /// <param name="page">Número de página (base 1, por defecto 1).</param>
    /// <param name="pageSize">Cantidad de registros por página (1 a 100, por defecto 20).</param>
    /// <param name="startDate">Fecha inicial del filtro (formato 'yyyy-MM-dd' o ISO 8601). Se interpreta según la hora legal de Venezuela (UTC-4, VET) desde las 00:00:00 locales.</param>
    /// <param name="endDate">Fecha final del filtro (formato 'yyyy-MM-dd' o ISO 8601). Se interpreta según la hora legal de Venezuela (UTC-4, VET) cubriendo hasta las 23:59:59.999 locales. Si se omite habiendo startDate, asume el día actual.</param>
    /// <param name="search">Término de búsqueda multicampo (N° factura, cédula/nombre cliente o cajero).</param>
    [HttpGet("history")]
    public async Task<ActionResult> GetHistory([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] System.DateTime? startDate = null, [FromQuery] System.DateTime? endDate = null, [FromQuery] string? search = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (_items, _total_count) = await _salesService.GetSalesHistoryAsync(page, pageSize, startDate, endDate, search);
        return Ok(new { Items = _items, TotalCount = _total_count });
    }

    [HttpGet("{id}/history-detail")]
    public async Task<ActionResult> GetHistoryDetail(int id)
    {
        try
        {
            var _detail = await _salesService.GetSaleHistoryDetailAsync(id);
            return Ok(_detail);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{id}/price-list")]
    public async Task<ActionResult<SaleDto>> UpdatePriceList(int id, [FromBody] UpdatePriceListRequestDto request)
    {
        try
        {
            var sale = await _salesService.UpdatePriceListAsync(id, request.PriceListType);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { Message = ex.Message });
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }
}

public class AddItemRequest
{
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal ExchangeRate { get; set; }
    public decimal? CustomUnitPriceUsd { get; set; }
    public decimal? CustomUnitPriceLocal { get; set; }
}

public class UpdateQuantityRequest
{
    public decimal Quantity { get; set; }
    public decimal ExchangeRate { get; set; }
}

public class CompleteSaleRequest
{
    public decimal ExchangeRate { get; set; }
    public decimal RoundingAdjustment { get; set; }
    public int? CashierId { get; set; }
    public bool IsPendingPickup { get; set; } = false;
    public IEnumerable<SalePaymentDto> Payments { get; set; } = new List<SalePaymentDto>();
}
public class UpdateSaleCustomerRequest
{
    public int CustomerId { get; set; }
}
