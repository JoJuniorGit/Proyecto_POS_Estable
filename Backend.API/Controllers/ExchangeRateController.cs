using Core.Entities;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Sales.Module.Interfaces;
using Backend.API.Hubs;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/exchange-rate")]
public class ExchangeRateController : ControllerBase
{
    private readonly InventoryDbContext _context;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;
    private readonly ISalesService _salesService;
    private readonly IHubContext<ExchangeRateHub> _hubContext;

    public ExchangeRateController(
        InventoryDbContext context,
        Core.Interfaces.ICurrentUserService currentUserService,
        ISalesService salesService,
        IHubContext<ExchangeRateHub> hubContext)
    {
        _context = context;
        _currentUserService = currentUserService;
        _salesService = salesService;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Returns today's exchange rate, or 0 if none has been set.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("today")]
    public async Task<ActionResult> GetToday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var record = await _context.ExchangeRateHistory
            .FirstOrDefaultAsync(r => r.Date == today);

        if (record == null)
            return Ok(new { Value = 0m, Date = today, UpdatedAt = (DateTime?)null });

        return Ok(new { Value = record.Rate, Date = record.Date, UpdatedAt = record.UpdatedAt });
    }

    /// <summary>
    /// Returns the complete exchange rate history sorted by date descending.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("history")]
    public async Task<ActionResult> GetHistory()
    {
        var history = await _context.ExchangeRateHistory
            .OrderByDescending(r => r.Date)
            .Select(r => new { r.Date, r.Rate, r.UpdatedAt })
            .ToListAsync();

        return Ok(history);
    }

    /// <summary>
    /// Upserts today's exchange rate. One record per day; overwrites if already set.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult> UpsertRate([FromBody] UpsertExchangeRateRequest request)
    {
        if (!_currentUserService.CanMutateExchangeRate)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para actualizar la tasa de cambio.");
        }
        if (request.Value <= 0)
            return BadRequest("Exchange rate must be greater than zero.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var existing = await _context.ExchangeRateHistory
            .FirstOrDefaultAsync(r => r.Date == today);

        if (existing != null)
        {
            existing.Rate = request.Value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _context.ExchangeRateHistory.Add(new ExchangeRateHistory
            {
                Date = today,
                Rate = request.Value,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();

        // Recalculate OnHold sales with the new exchange rate
        await _salesService.RecalculateOnHoldSalesAsync(request.Value);

        // Broadcast rate update and OnHold sales refresh signal to all connected clients
        await _hubContext.Clients.All.SendAsync("ReceiveRateUpdate", request.Value);
        await _hubContext.Clients.All.SendAsync("OnHoldSalesUpdated");

        return Ok(new { Value = request.Value, Date = today, UpdatedAt = DateTime.UtcNow });
    }

    /// <summary>
    /// Forces a manual scrape of the BCV website and upserts today's exchange rate.
    /// </summary>
    [HttpPost("sync-bcv")]
    public async Task<ActionResult> SyncBcv([FromServices] Backend.API.Services.BcvScraperService scraperService)
    {
        if (!_currentUserService.CanMutateExchangeRate)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para sincronizar la tasa de cambio.");
        }
        var rate = await scraperService.GetOfficialUsdRateAsync();
        if (!rate.HasValue)
        {
            return StatusCode(500, "Failed to extract the official exchange rate from BCV.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var existing = await _context.ExchangeRateHistory
            .FirstOrDefaultAsync(r => r.Date == today);

        if (existing != null)
        {
            existing.Rate = rate.Value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _context.ExchangeRateHistory.Add(new ExchangeRateHistory
            {
                Date = today,
                Rate = rate.Value,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();

        // Recalculate OnHold sales with the new exchange rate
        await _salesService.RecalculateOnHoldSalesAsync(rate.Value);

        // Broadcast to clients
        await _hubContext.Clients.All.SendAsync("ReceiveRateUpdate", rate.Value);
        await _hubContext.Clients.All.SendAsync("OnHoldSalesUpdated");

        return Ok(new { Value = rate.Value, Date = today, UpdatedAt = DateTime.UtcNow });
    }
}

public class UpsertExchangeRateRequest
{
    public decimal Value { get; set; }
}
