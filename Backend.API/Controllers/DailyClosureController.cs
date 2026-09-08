using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Inventory.Module.Data;
using Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Attributes;
using Backend.API.Services;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DailyClosureController : ControllerBase
{
    private readonly IDailyClosureService _closureService;
    private readonly ICashDrawerService _cashDrawerService;
    private readonly InventoryDbContext _inventoryContext;
    private readonly ISystemSettingsService _settingsService;
    private readonly SalesDbContext _salesContext;
    private readonly ICurrentUserService _currentUserService;

    public DailyClosureController(
        IDailyClosureService closureService,
        ICashDrawerService cashDrawerService,
        InventoryDbContext inventoryContext,
        ISystemSettingsService settingsService,
        SalesDbContext salesContext,
        ICurrentUserService currentUserService)
    {
        _closureService = closureService;
        _cashDrawerService = cashDrawerService;
        _inventoryContext = inventoryContext;
        _settingsService = settingsService;
        _salesContext = salesContext;
        _currentUserService = currentUserService;
    }

    // 8.7-M3: tasa efectiva del día centralizada en ExchangeRateResolver (BCV hoy -> histórico -> apertura de sesión).
    private Task<decimal> GetTodayExchangeRateAsync()
    {
        return ExchangeRateResolver.ReadEffectiveTodayRateAsync(_inventoryContext, _cashDrawerService);
    }

    [HttpGet("expected-totals")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<List<ExpectedTotalDto>>> GetExpectedTotals([FromQuery] DateTime dateUtc)
    {
        var totals = await _closureService.GetExpectedTotalsByPaymentMethodAsync(dateUtc);
        return Ok(totals);
    }

    [RequireSecurityStampValidation]
    [HttpPost]
    public async Task<ActionResult> CreateClosure([FromBody] CreateClosureRequest request)
    {
        if (User.IsInRole("Driver"))
        {
            return Forbid();
        }

        if (request == null || request.Details == null || !request.Details.Any())
        {
            return BadRequest(new { message = "El arqueo debe incluir el desglose por métodos de pago." });
        }

        try
        {
            // 8.9-B4: el cierre diario combina SalesDbContext (totales/cierre) + caja (rollover) en una
            // transacción Serializable cross-DB; bajo execution strategy para reintentar en bloque
            // ante fallos transitorios.
            return await _salesContext.Database.CreateExecutionStrategy().ExecuteAsync<ActionResult>(async () =>
            {
            // 1. Identidad autoritativa por claims (H-API-2)
            string? authenticatedUserId = _currentUserService.UserId
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.Identity?.Name;

            var finalUserId = !string.IsNullOrWhiteSpace(authenticatedUserId)
                ? authenticatedUserId
                : (!string.IsNullOrWhiteSpace(request.UserId) ? request.UserId : "Admin");

            DateTime closureDate = DateTime.UtcNow;
            if (request.ClosureDate != default)
            {
                var now = DateTime.UtcNow;
                var requestedDateUtc = request.ClosureDate.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(request.ClosureDate, DateTimeKind.Utc)
                    : request.ClosureDate.ToUniversalTime();

                bool isAdmin = User.IsInRole("Admin");
                if (isAdmin)
                {
                    if (requestedDateUtc > now.AddMinutes(5))
                    {
                        return BadRequest(new { message = "La fecha de cierre no puede ser en el futuro." });
                    }
                    if (now - requestedDateUtc > TimeSpan.FromHours(24))
                    {
                        return BadRequest(new { message = "No se permite registrar cierres con más de 24 horas de retroactividad." });
                    }

                    closureDate = requestedDateUtc;
                    Core.Logging.AppLogger.LogSecurityAudit(
                        $"[DAILY_CLOSURE_BACKDATE] Admin '{finalUserId}' registró un cierre con fecha retroactiva: {closureDate:O} (Actual: {now:O})");
                }
                else
                {
                    closureDate = now;
                }
            }

            decimal exchangeRate = await GetTodayExchangeRateAsync();

            // 8.2-M2: Bloquear cierre sin tasa BCV del día (o tasa 0/NA explícita) con error claro
            if (exchangeRate <= 0)
            {
                throw new InvalidOperationException("No se puede registrar el cierre diario: no existe una tasa BCV registrada para hoy. Registre la tasa del día antes de cerrar la caja.");
            }

            using var dbTransaction = await _salesContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            // 2. Totales esperados autoritativos calculados server-side (H-API-3) dentro de la transacción Serializable
            var serverExpectedTotals = await _closureService.GetExpectedTotalsByPaymentMethodAsync(closureDate);
            var expectedMap = serverExpectedTotals.ToDictionary(e => e.PaymentMethodId, e => e.ExpectedAmountBsS);

            var closure = new DailyClosure
            {
                ClosureDate = closureDate,
                UserId = finalUserId,
                Observation = request.Observation,
                Details = request.Details.Select(d =>
                {
                    expectedMap.TryGetValue(d.PaymentMethodId, out var authoritativeExpected);
                    return new ClosureDetail
                    {
                        PaymentMethodId = d.PaymentMethodId,
                        PaymentMethodName = d.PaymentMethodName,
                        ExpectedAmountBsS = authoritativeExpected,
                        ActualAmountBsS = d.ActualAmountBsS
                    };
                }).ToList()
            };

            var result = await _closureService.CreateClosureAsync(closure);

            // Al cerrar el turno, se cierra la sesión anterior y se inicia una nueva conservando el saldo esperado
            // en caja (saldo teórico acumulado) pero reiniciando a 0 los acumuladores de ingresos y egresos de la sesión.
            await _cashDrawerService.RolloverSessionAfterClosureAsync(exchangeRate);

            await dbTransaction.CommitAsync();

            // 8.7-B5: los comprobantes (PDF/TXT) se escriben DESPUÉS del commit para no mantener
            // abierta la transacción Serializable durante I/O de disco.
            _closureService.WriteClosedClosureReceipts(result);

            return Ok(result);
            });
        }
        catch (DbUpdateException)
        {
            return this.ApiConflict("Conflicto de concurrencia al registrar el cierre diario. Es posible que ya se haya ejecutado otro cierre en paralelo.");
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [HttpGet("{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult> GetClosure(int id)
    {
        var closure = await _closureService.GetClosureAsync(id);
        if (closure == null) return NotFound();
        return Ok(closure);
    }
}

public class CreateClosureRequest
{
    public DateTime ClosureDate { get; set; }
    public string? UserId { get; set; } = "Admin";
    public string? Observation { get; set; }
    public List<CreateClosureDetailRequest> Details { get; set; } = new();
}

public class CreateClosureDetailRequest
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal ExpectedAmountBsS { get; set; }
    public decimal ActualAmountBsS { get; set; }
}
