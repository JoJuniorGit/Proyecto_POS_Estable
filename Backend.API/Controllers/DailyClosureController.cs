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

    private async Task<decimal> GetTodayExchangeRateAsync()
    {
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var record = await _inventoryContext.ExchangeRateHistory
            .FirstOrDefaultAsync(r => r.Date == today);

        if (record == null)
        {
            record = await _inventoryContext.ExchangeRateHistory
                .Where(r => r.Date <= today)
                .OrderByDescending(r => r.Date)
                .FirstOrDefaultAsync();
        }

        if (record != null && record.Rate > 0)
            return record.Rate;

        return 1.0m;
    }

    [HttpGet("expected-totals")]
    public async Task<ActionResult<List<ExpectedTotalDto>>> GetExpectedTotals([FromQuery] DateTime dateUtc)
    {
        var totals = await _closureService.GetExpectedTotalsByPaymentMethodAsync(dateUtc);
        return Ok(totals);
    }

    [RequireSecurityStampValidation]
    [HttpPost]
    public async Task<ActionResult> CreateClosure([FromBody] CreateClosureRequest request)
    {
        try
        {
            // 1. Identidad fidedigna por claims autenticados (H-API-2)
            var authenticatedUserId = _currentUserService.UserId 
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

            // 2. Totales esperados autoritativos calculados server-side (H-API-3)
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

            decimal exchangeRate = await GetTodayExchangeRateAsync();

            using var dbTransaction = await _salesContext.Database.BeginTransactionAsync();

            var result = await _closureService.CreateClosureAsync(closure);

            // Al cerrar el turno, se cierra la sesión anterior y se inicia una nueva conservando el saldo esperado
            // en caja (saldo teórico acumulado) pero reiniciando a 0 los acumuladores de ingresos y egresos de la sesión.
            await _cashDrawerService.RolloverSessionAfterClosureAsync(exchangeRate);

            await dbTransaction.CommitAsync();

            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
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
