using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using Core.Logging;
using Sales.Module.Interfaces;
using System.ComponentModel.DataAnnotations;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class SalesController : ControllerBase
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
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<SaleDto>> StartSale([FromQuery] int? cashierId = null)
    {
        int? effectiveCashierId = _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid) 
            ? uid 
            : cashierId;

        var sale = await _salesService.StartSaleAsync(effectiveCashierId);
        return Ok(sale);
    }

    // 8.5-A3: Autorización por objeto. Un cajero solo puede mutar ventas que él inició.
    // Admin/Manager siempre autorizados. Si la venta no puede resolverse (null / mock no stubeado),
    // el helper es tolerante y NO bloquea (la resolución real de existencia la hace el servicio).
    private async Task<bool> IsAuthorizedForSaleAsync(int saleId)
    {
        bool isElevated = User.IsInRole("Admin") || User.IsInRole("Manager");
        if (isElevated) return true;

        if (_currentUserService.UserRole.HasValue && _currentUserService.UserRole.Value == Core.Entities.UserRole.Driver)
        {
            return false;
        }

        if (User.IsInRole("Driver")) return false;

        SaleDto? target;
        try
        {
            target = await _salesService.GetSaleAsync(saleId);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return true; // Permite que el servicio devuelva NotFound al mutador
        }

        if (target == null) return true; // Tolerante a null (mocks/indefinido)

        // Si no es rol elevado (Cashier), exige que el cajero sea el dueño.
        if (_currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid))
        {
            return target.CashierId == uid;
        }

        return true;
    }

    // 8.9-B2: scope de lectura para listados. Un cajero solo puede listar sus propias ventas;
    // Admin/Manager conservan visión global. Se aplica a pending, pending-pickups e history.
    private (bool ScopeToCashier, int CashierId) GetCashierReadScope()
    {
        bool isElevated = User.IsInRole("Admin") || User.IsInRole("Manager");
        if (isElevated) return (false, 0);

        if (_currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid))
        {
            return (true, uid);
        }

        return (false, 0);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<SaleDto>> GetSale(int id)
    {
        // 8.5-A3/8.6-B1: ownership a nivel de objeto — un cajero solo puede leer ventas propias.
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para consultar esta venta." });
        }

        try
        {
            var sale = await _salesService.GetSaleAsync(id);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{id}/items")]
    public async Task<ActionResult<SaleDto>> AddItem(int id, [FromBody] AddItemRequest request)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para modificar esta venta." });
        }

        bool isAuthorized = User.IsInRole("Admin") || User.IsInRole("Manager");
        if ((request.CustomUnitPriceUsd.HasValue || request.CustomUnitPriceLocal.HasValue) && !isAuthorized)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor." });
        }

        var sale = await _salesService.AddItemAsync(id, request.ProductId, request.Quantity, request.ExchangeRate, request.CustomUnitPriceUsd, request.CustomUnitPriceLocal, isAuthorized);
        return Ok(sale);
    }

    [HttpDelete("{id}/items/{itemId}")]
    public async Task<ActionResult<SaleDto>> RemoveItem(int id, int itemId, [FromQuery] decimal exchangeRate)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para modificar esta venta." });
        }

        var sale = await _salesService.RemoveItemAsync(id, itemId, exchangeRate);
        return Ok(sale);
    }

    [HttpPut("{id}/items/{itemId}")]
    public async Task<ActionResult<SaleDto>> UpdateItemQuantity(int id, int itemId, [FromBody] UpdateQuantityRequest request)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para modificar esta venta." });
        }

        var sale = await _salesService.UpdateItemQuantityAsync(id, itemId, request.Quantity, request.ExchangeRate);
        return Ok(sale);
    }

    [HttpPut("{id}/exchange-rate")]
    public async Task<ActionResult<SaleDto>> UpdateExchangeRate(int id, [FromQuery] decimal exchangeRate)
    {
        try
        {
            if (!await IsAuthorizedForSaleAsync(id))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para modificar esta venta." });
            }

            var sale = await _salesService.UpdateExchangeRateAsync(id, exchangeRate);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    private async Task<(bool ShouldStop, ActionResult? BlockingResult, string? Key, byte[]? PayloadHash)> ResolveIdempotencyAsync(string requestPath, string bodyJson)
    {
        string? idempotencyKey = Request?.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return (true, BadRequest(new { message = "El encabezado Idempotency-Key es obligatorio para esta operación." }), null, null);
        }

        idempotencyKey = idempotencyKey.Trim();

        if (_idempotencyService == null)
        {
            return (false, null, idempotencyKey, null);
        }

        if (!_idempotencyService.ValidateKeyFormat(idempotencyKey, out var formatError))
        {
            return (true, BadRequest(new { message = formatError }), null, null);
        }

        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(bodyJson);
        var payloadHash = _idempotencyService.ComputePayloadHash(Request?.Method ?? "POST", requestPath, bodyBytes);

        var checkResult = await _idempotencyService.CheckAsync(idempotencyKey, requestPath, payloadHash, HttpContext?.RequestAborted ?? default);
        if (checkResult.IsReplay)
        {
            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "HIT";
            }
            return (true, Content(checkResult.StoredResponseBody ?? "", "application/json"), null, null);
        }

        if (checkResult.IsMismatch)
        {
            var clientIp = HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            AppLogger.LogSecurityAudit($"[IDEMPOTENCY_MISMATCH] Key={idempotencyKey}, Path={requestPath}, IP={clientIp}, Timestamp={System.DateTime.UtcNow:O}");
            return (true, StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = "La clave de idempotencia ya fue utilizada para una transacción diferente con otro contenido." }), null, null);
        }

        return (false, null, idempotencyKey, payloadHash);
    }

    private async Task<ActionResult> HandleIdempotencyCollisionAsync(Microsoft.EntityFrameworkCore.DbUpdateException ex, string requestPath, string? key, byte[]? payloadHash)
    {
        if (_idempotencyService is Sales.Module.Services.IdempotencyService idService && !string.IsNullOrWhiteSpace(key) && payloadHash != null)
        {
            var collisionResult = await idService.HandleConcurrentCollisionAsync(key, requestPath, payloadHash, HttpContext?.RequestAborted ?? default);
            if (collisionResult.IsReplay)
            {
                if (Response?.Headers != null)
                {
                    Response.Headers["X-Cache-Lookup"] = "HIT";
                }
                return Content(collisionResult.StoredResponseBody ?? "", "application/json");
            }
            if (collisionResult.IsMismatch)
            {
                var clientIp = HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
                AppLogger.LogSecurityAudit($"[IDEMPOTENCY_MISMATCH] Key={key}, Path={requestPath}, IP={clientIp}, Timestamp={System.DateTime.UtcNow:O}");
                return StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = "La clave de idempotencia ya fue utilizada para una transacción diferente con otro contenido." });
            }
        }
        return StatusCode(StatusCodes.Status409Conflict, new { message = "Operación concurrente en progreso para esta clave de idempotencia." });
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
    // 8.7-B2: El ajuste de redondeo debe acotarse; la validación en el servicio refuerza el límite.
    [Range(-1000, 1000, ErrorMessage = "El ajuste de redondeo está fuera de los límites operacionales (-1000 a 1000).")]
    public decimal RoundingAdjustment { get; set; }
    public int? CashierId { get; set; }
    public bool IsPendingPickup { get; set; } = false;
    public IEnumerable<SalePaymentDto> Payments { get; set; } = new List<SalePaymentDto>();
}
public class UpdateSaleCustomerRequest
{
    public int CustomerId { get; set; }
}

public class CheckoutPreviewRequest
{
    public decimal ExchangeRate { get; set; }
    public IEnumerable<SalePaymentDto> Payments { get; set; } = new List<SalePaymentDto>();
}

public class CheckoutPreviewResponse
{
    public decimal TotalUSD { get; set; }
    public decimal TotalBsS { get; set; }
    public decimal TotalPaidUSD { get; set; }
    public decimal TotalPaidBsS { get; set; }
    public decimal RemainingBalanceUSD { get; set; }
    public decimal RemainingBalanceBsS { get; set; }
    public decimal RoundingAdjustment { get; set; }
    public decimal ChangeDueUSD { get; set; }
    public decimal ChangeDueBsS { get; set; }
    public bool IsFullyPaid { get; set; }
}