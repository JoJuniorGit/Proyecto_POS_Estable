using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Backend.API.Services;
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
    private const string PriceOverrideForbiddenMessage = "Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor.";
    private const string AuthorizationTokenHeader = "X-Authorization-Token";

    // 8.150 (T5, design D5): extensiones exactas del contrato 403 del gate de precio manual.
    private static readonly System.Collections.Generic.IReadOnlyDictionary<string, object?> ManualPriceOverrideExtensions =
        new System.Collections.Generic.Dictionary<string, object?>
        {
            ["authorizationRequired"] = true,
            ["authorizationAction"] = "ManualPriceOverride"
        };

    private readonly ISalesService _salesService;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;
    private readonly Core.Interfaces.IIdempotencyService? _idempotencyService;
    // 8.150 (T5, design D5): consumo del token efimero para acciones protegidas. Opcional para
    // preservar la construccion directa historica de tests; si falta, el gate falla cerrado.
    private readonly IAuthorizationCoordinator? _authorizationCoordinator;
    // 8.159 (CLEAN-04): cálculo del checkout-preview extraído del controlador a Sales.Module.
    // Opcional para preservar la construccion directa historica de tests (precedente 8.149/8.150);
    // el endpoint de preview lo consume y DI lo resuelve en produccion.
    private readonly ICheckoutCalculationService? _checkoutCalculator;
    // 8.149 (SRE-02): resolutor compartido extraído del método privado histórico.
    private readonly IdempotencyRequestResolver _idempotencyResolver;

    public SalesController(
        ISalesService salesService, 
        Core.Interfaces.ICurrentUserService currentUserService,
        Core.Interfaces.IIdempotencyService? idempotencyService = null,
        IAuthorizationCoordinator? authorizationCoordinator = null,
        ICheckoutCalculationService? checkoutCalculationService = null)
    {
        _salesService = salesService;
        _currentUserService = currentUserService;
        _idempotencyService = idempotencyService;
        _authorizationCoordinator = authorizationCoordinator;
        _checkoutCalculator = checkoutCalculationService;
        _idempotencyResolver = new IdempotencyRequestResolver(idempotencyService, currentUserService);
    }

    [HttpPost("start")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<SaleDto>> StartSaleAsync([FromQuery] int? cashierId = null, CancellationToken cancellationToken = default)
    {
        int? effectiveCashierId = _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid) 
            ? uid 
            : cashierId;

        var sale = await _salesService.StartSaleAsync(effectiveCashierId, cancellationToken);
        return Ok(sale);
    }

    private async Task<bool> IsAuthorizedForSaleAsync(int saleId, CancellationToken cancellationToken)
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
            target = await _salesService.GetSaleAsync(saleId, cancellationToken);
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
        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para consultar esta venta.");
        }

        try
        {
            var sale = await _salesService.GetSaleAsync(id, cancellationToken);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return this.ApiNotFound($"Venta con ID {id} no encontrada.");
        }
    }

    [HttpPost("{id}/items")]
    public async Task<ActionResult<SaleDto>> AddItemAsync(int id, [FromBody] AddItemRequest request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        bool isAuthorized = User.IsInRole("Admin") || User.IsInRole("Manager");
        bool hasCustomPrice = request.CustomUnitPriceUsd.HasValue || request.CustomUnitPriceLocal.HasValue;

        // 8.150 (T5, design D5): el precio manual de un usuario no elevado exige un token
        // efimero single-use. El gate corre ANTES de la idempotencia para todos los rechazos:
        // sin token, token invalido/reusado/expirado o contexto distinto no hay mutacion ni
        // efecto de idempotencia. Un token ausente y sin precio custom se ignora.
        if (hasCustomPrice && !isAuthorized)
        {
            string? authorizationToken = Request.Headers.TryGetValue(AuthorizationTokenHeader, out var headerValues)
                ? headerValues.ToString()
                : null;

            if (string.IsNullOrWhiteSpace(authorizationToken) || _authorizationCoordinator is null)
            {
                return this.ApiForbidden(PriceOverrideForbiddenMessage, ManualPriceOverrideExtensions);
            }

            var operation = new ManualPriceOverrideContext
            {
                ProductId = request.ProductId,
                Quantity = request.Quantity,
                CustomUnitPriceUsd = request.CustomUnitPriceUsd,
                CustomUnitPriceLocal = request.CustomUnitPriceLocal
            };

            var consume = await _authorizationCoordinator.ConsumeAsync(authorizationToken, id, GetActorUserId(), operation, cancellationToken);

            if (consume.Status == AuthorizationConsumeStatus.Consumed)
            {
                isAuthorized = true;
            }
            else if (consume.Status == AuthorizationConsumeStatus.AlreadyConsumed)
            {
                // D5: con el token ya consumido, un reintento con la misma clave debe replicar
                // la respuesta almacenada (HIT) en vez de fallar; un MISS devuelve el 403 sin
                // ejecutar. La idempotencia se resuelve una sola vez por peticion.
                string replayPath = $"/api/sales/{id}/items";
                string replayBodyJson = GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);
                var replay = await _idempotencyResolver.ResolveAsync(this, replayPath, replayBodyJson);
                if (replay.ShouldStop) return replay.BlockingResult!;

                return this.ApiForbidden(PriceOverrideForbiddenMessage, ManualPriceOverrideExtensions);
            }
            else
            {
                return this.ApiForbidden(PriceOverrideForbiddenMessage, ManualPriceOverrideExtensions);
            }
        }

        // 8.149 (SRE-02): clave obligatoria + replay/422 antes de tocar la venta.
        string requestPath = $"/api/sales/{id}/items";
        string bodyJson = GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);
        var resolved = await _idempotencyResolver.ResolveAsync(this, requestPath, bodyJson);
        if (resolved.ShouldStop) return resolved.BlockingResult!;

        try
        {
            var sale = await _salesService.AddItemAsync(id, request.ProductId, request.Quantity, request.ExchangeRate, request.CustomUnitPriceUsd, request.CustomUnitPriceLocal, isAuthorized, GetActorUserId(), cancellationToken);
            await _idempotencyResolver.RegisterSuccessAsync(resolved, requestPath, System.Text.Json.JsonSerializer.Serialize(sale), cancellationToken);

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            return Ok(sale);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IdempotencyRequestResolver.IsIdempotencyUniqueViolation(ex))
        {
            return await _idempotencyResolver.HandleCollisionAsync(this, ex, requestPath, resolved.Key, resolved.PayloadHash);
        }
    }

    [HttpDelete("{id}/items/{itemId}")]
    public async Task<ActionResult<SaleDto>> RemoveItemAsync(int id, int itemId, [FromQuery] string exchangeRate, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        var sale = await _salesService.RemoveItemAsync(id, itemId, ParseRateInvariant(exchangeRate), GetActorUserId(), cancellationToken);
        return Ok(sale);
    }

    [HttpPut("{id}/items/{itemId}")]
    public async Task<ActionResult<SaleDto>> UpdateItemQuantityAsync(int id, int itemId, [FromBody] UpdateQuantityRequest request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        var sale = await _salesService.UpdateItemQuantityAsync(id, itemId, request.Quantity, request.ExchangeRate, GetActorUserId(), cancellationToken);
        return Ok(sale);
    }

    [HttpPut("{id}/exchange-rate")]
    public async Task<ActionResult<SaleDto>> UpdateExchangeRateAsync(int id, [FromQuery] string exchangeRate, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
            {
                return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
            }

            var sale = await _salesService.UpdateExchangeRateAsync(id, ParseRateInvariant(exchangeRate), GetActorUserId(), cancellationToken);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    internal static decimal ParseRateInvariant(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rate)
            ? rate
            : 0m;
    }
}