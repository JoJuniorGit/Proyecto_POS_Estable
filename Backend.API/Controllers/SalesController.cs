using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using Core.Logging;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

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
    public async Task<ActionResult<SaleDto>> StartSaleAsync([FromQuery] int? cashierId = null, CancellationToken cancellationToken = default)
    {
        int? effectiveCashierId = _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid) 
            ? uid 
            : cashierId;

        var sale = await _salesService.StartSaleAsync(effectiveCashierId);
        return Ok(sale);
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> StartSale([FromQuery] int? cashierId = null) => StartSaleAsync(cashierId);

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
            return true;
        }

        if (target == null) return true;

        if (string.Equals(target.Status, "OnHold", StringComparison.Ordinal)) return true;

        if (_currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid))
        {
            return target.CashierId == uid;
        }

        return true;
    }

    private int? GetActorUserId()
    {
        return _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid)
            ? uid
            : null;
    }

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
    public async Task<ActionResult<SaleDto>> GetSaleAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para consultar esta venta.");
        }

        try
        {
            var sale = await _salesService.GetSaleAsync(id);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return this.ApiNotFound($"Venta con ID {id} no encontrada.");
        }
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> GetSale(int id) => GetSaleAsync(id);

    [HttpPost("{id}/items")]
    public async Task<ActionResult<SaleDto>> AddItemAsync(int id, [FromBody] AddItemRequest request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        bool isAuthorized = User.IsInRole("Admin") || User.IsInRole("Manager");
        if ((request.CustomUnitPriceUsd.HasValue || request.CustomUnitPriceLocal.HasValue) && !isAuthorized)
        {
            return this.ApiForbidden("Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor.");
        }

        var sale = await _salesService.AddItemAsync(id, request.ProductId, request.Quantity, request.ExchangeRate, request.CustomUnitPriceUsd, request.CustomUnitPriceLocal, isAuthorized, GetActorUserId());
        return Ok(sale);
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> AddItem(int id, [FromBody] AddItemRequest request) => AddItemAsync(id, request);

    [HttpDelete("{id}/items/{itemId}")]
    public async Task<ActionResult<SaleDto>> RemoveItemAsync(int id, int itemId, [FromQuery] string exchangeRate, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        var sale = await _salesService.RemoveItemAsync(id, itemId, ParseRateInvariant(exchangeRate), GetActorUserId());
        return Ok(sale);
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> RemoveItem(int id, int itemId, [FromQuery] string exchangeRate) => RemoveItemAsync(id, itemId, exchangeRate);

    [HttpPut("{id}/items/{itemId}")]
    public async Task<ActionResult<SaleDto>> UpdateItemQuantityAsync(int id, int itemId, [FromBody] UpdateQuantityRequest request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        var sale = await _salesService.UpdateItemQuantityAsync(id, itemId, request.Quantity, request.ExchangeRate, GetActorUserId());
        return Ok(sale);
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> UpdateItemQuantity(int id, int itemId, [FromBody] UpdateQuantityRequest request) => UpdateItemQuantityAsync(id, itemId, request);

    [HttpPut("{id}/exchange-rate")]
    public async Task<ActionResult<SaleDto>> UpdateExchangeRateAsync(int id, [FromQuery] string exchangeRate, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await IsAuthorizedForSaleAsync(id))
            {
                return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
            }

            var sale = await _salesService.UpdateExchangeRateAsync(id, ParseRateInvariant(exchangeRate), GetActorUserId());
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> UpdateExchangeRate(int id, [FromQuery] string exchangeRate) => UpdateExchangeRateAsync(id, exchangeRate);

    private async Task<(bool ShouldStop, ActionResult? BlockingResult, string? Key, byte[]? PayloadHash)> ResolveIdempotencyAsync(string requestPath, string bodyJson)
    {
        string? idempotencyKey = Request?.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return (true, this.ApiBadRequest("El encabezado Idempotency-Key es obligatorio para esta operación."), null, null);
        }

        idempotencyKey = idempotencyKey.Trim();

        if (_idempotencyService == null)
        {
            return (false, null, idempotencyKey, null);
        }

        if (!_idempotencyService.ValidateKeyFormat(idempotencyKey, out var formatError))
        {
            return (true, this.ApiBadRequest(formatError), null, null);
        }

        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(bodyJson);
        var payloadHash = _idempotencyService.ComputePayloadHash(Request?.Method ?? "POST", requestPath, bodyBytes);

        var checkResult = await _idempotencyService.CheckAsync(idempotencyKey, requestPath, payloadHash, GetActorUserId(), HttpContext?.RequestAborted ?? default);
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
            return (true, this.ApiUnprocessableEntity("La clave de idempotencia ya fue utilizada para una transacción diferente con otro contenido."), null, null);
        }

        return (false, null, idempotencyKey, payloadHash);
    }

    private async Task<ActionResult> HandleIdempotencyCollisionAsync(Microsoft.EntityFrameworkCore.DbUpdateException ex, string requestPath, string? key, byte[]? payloadHash)
    {
        if (_idempotencyService is Sales.Module.Services.IdempotencyService idService && !string.IsNullOrWhiteSpace(key) && payloadHash != null)
        {
            var collisionResult = await idService.HandleConcurrentCollisionAsync(key, requestPath, payloadHash, GetActorUserId(), HttpContext?.RequestAborted ?? default);
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
                return this.ApiUnprocessableEntity("La clave de idempotencia ya fue utilizada para una transacción diferente con otro contenido.");
            }
        }
        return this.ApiConflict("Operación concurrente en progreso para esta clave de idempotencia.");
    }

    internal static decimal ParseRateInvariant(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rate)
            ? rate
            : 0m;
    }
}