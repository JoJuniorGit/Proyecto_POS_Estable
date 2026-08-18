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

    public DailyClosureController(
        IDailyClosureService closureService,
        ICashDrawerService cashDrawerService,
        InventoryDbContext inventoryContext,
        ISystemSettingsService settingsService,
        SalesDbContext salesContext)
    {
        _closureService = closureService;
        _cashDrawerService = cashDrawerService;
        _inventoryContext = inventoryContext;
        _settingsService = settingsService;
        _salesContext = salesContext;
    }

    private async Task<decimal> GetTodayExchangeRateAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var record = await _inventoryContext.ExchangeRateHistory
            .FirstOrDefaultAsync(r => r.Date == today);

        if (record == null)
        {
            record = await _inventoryContext.ExchangeRateHistory
                .OrderByDescending(r => r.Date)
                .FirstOrDefaultAsync();
        }

        if (record != null && record.Rate > 0)
            return record.Rate;

        var rateStr = await _settingsService.GetSettingAsync("CurrentExchangeRate") ?? "1.0";
        if (decimal.TryParse(rateStr, out decimal parsedRate) && parsedRate > 0)
            return parsedRate;

        return 1.0m;
    }

    [HttpGet("expected-totals")]
    public async Task<ActionResult<List<ExpectedTotalDto>>> GetExpectedTotals([FromQuery] DateTime dateUtc)
    {
        var totals = await _closureService.GetExpectedTotalsByPaymentMethodAsync(dateUtc);
        return Ok(totals);
    }

    [HttpPost]
    public async Task<ActionResult> CreateClosure([FromBody] CreateClosureRequest request)
    {
        try
        {
            var closure = new DailyClosure
            {
                ClosureDate = request.ClosureDate,
                UserId = request.UserId,
                Observation = request.Observation,
                Details = request.Details.Select(d => new ClosureDetail
                {
                    PaymentMethodId = d.PaymentMethodId,
                    PaymentMethodName = d.PaymentMethodName,
                    ExpectedAmountBsS = d.ExpectedAmountBsS,
                    ActualAmountBsS = d.ActualAmountBsS
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
