using Microsoft.AspNetCore.Mvc;
using Sales.Module.Interfaces;
using Sales.Module.Data;
using Sales.Module.Entities;
using Inventory.Module.Data;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.Linq;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/shifts")]
public class ShiftsController : ControllerBase
{
    private readonly ICashDrawerService _cashDrawerService;
    private readonly IDailyClosureService _dailyClosureService;
    private readonly IPaymentMethodService _paymentMethodService;
    private readonly ISystemSettingsService _settingsService;
    private readonly InventoryDbContext _inventoryContext;
    private readonly SalesDbContext _salesContext;

    public ShiftsController(
        ICashDrawerService cashDrawerService,
        IDailyClosureService dailyClosureService,
        IPaymentMethodService paymentMethodService,
        ISystemSettingsService settingsService,
        InventoryDbContext inventoryContext,
        SalesDbContext salesContext)
    {
        _cashDrawerService = cashDrawerService;
        _dailyClosureService = dailyClosureService;
        _paymentMethodService = paymentMethodService;
        _settingsService = settingsService;
        _inventoryContext = inventoryContext;
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

    [HttpPost("close")]
    public async Task<ActionResult> CloseShift([FromBody] CloseShiftRequest request)
    {
        try
        {
            decimal exchangeRate = await GetTodayExchangeRateAsync();

            // Obtenemos los totales teóricos por método de pago
            var expectedTotals = await _dailyClosureService.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);
            
            var details = new List<ShiftReportDetailDto>();
            foreach (var declared in request.DeclaredAmounts)
            {
                var expected = expectedTotals.FirstOrDefault(e => e.PaymentMethodId == declared.PaymentMethodId);
                decimal expectedSystemAmount = 0m;

                if (expected != null)
                {
                    if (declared.Currency == "USD")
                    {
                        expectedSystemAmount = exchangeRate > 0 ? (expected.ExpectedAmountBsS / exchangeRate) : 0m;
                    }
                    else
                    {
                        expectedSystemAmount = expected.ExpectedAmountBsS;
                    }
                }

                decimal diff = declared.Amount - expectedSystemAmount;
                string status = Math.Abs(diff) < 0.05m ? "Balanced" : (diff > 0 ? "Surplus" : "Shortage");

                details.Add(new ShiftReportDetailDto
                {
                    PaymentMethodId = declared.PaymentMethodId,
                    PaymentMethodName = declared.PaymentMethodName,
                    Currency = declared.Currency,
                    DeclaredAmount = declared.Amount,
                    SystemAmount = expectedSystemAmount,
                    Difference = diff,
                    Status = status
                });
            }

            using var dbTransaction = await _salesContext.Database.BeginTransactionAsync();

            // Persistir cierre de caja de forma secuencial en la Base de Datos
            var dailyClosure = new DailyClosure
            {
                ClosureDate = DateTime.UtcNow,
                UserId = request.CashierName ?? "Cajero Activo",
                Observation = request.CashierCedula ?? "V-00000000",
                Details = details.Select(d => new ClosureDetail
                {
                    PaymentMethodId = d.PaymentMethodId,
                    PaymentMethodName = d.PaymentMethodName,
                    ExpectedAmountBsS = d.Currency == "USD" ? d.SystemAmount * exchangeRate : d.SystemAmount,
                    ActualAmountBsS = d.Currency == "USD" ? d.DeclaredAmount * exchangeRate : d.DeclaredAmount,
                    DifferenceBsS = d.Currency == "USD" ? d.Difference * exchangeRate : d.Difference
                }).ToList()
            };

            var savedClosure = await _dailyClosureService.CreateClosureAsync(dailyClosure);

            await dbTransaction.CommitAsync();

            var report = new ShiftReportDto
            {
                ShiftId = savedClosure.Id,
                CashierName = request.CashierName ?? "Cajero Activo",
                CashierCedula = request.CashierCedula ?? "V-00000000",
                ClosedAt = savedClosure.ClosureDate,
                ExchangeRate = exchangeRate,
                Details = details
            };

            return Ok(report);
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpGet("current/report")]
    public async Task<ActionResult> GetCurrentReport()
    {
        var latestClosure = await _salesContext.DailyClosures
            .Include(c => c.Details)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync();

        if (latestClosure != null)
        {
            return await GetReportById(latestClosure.Id);
        }

        decimal exchangeRate = await GetTodayExchangeRateAsync();
        var expectedTotals = await _dailyClosureService.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);

        var details = expectedTotals.Select(e => new ShiftReportDetailDto
        {
            PaymentMethodId = e.PaymentMethodId,
            PaymentMethodName = e.PaymentMethodName,
            Currency = (e.PaymentMethodName.ToLower().Contains("usd") || e.PaymentMethodName.ToLower().Contains("dolar") || e.PaymentMethodName.Contains("$")) ? "USD" : "Bs.S",
            DeclaredAmount = 0m,
            SystemAmount = e.ExpectedAmountBsS,
            Difference = -e.ExpectedAmountBsS,
            Status = "Shortage"
        }).ToList();

        return Ok(new ShiftReportDto
        {
            ShiftId = 1,
            CashierName = "Cajero Activo",
            CashierCedula = "V-00000000",
            ClosedAt = DateTime.UtcNow,
            ExchangeRate = exchangeRate,
            Details = details
        });
    }

    [HttpGet("{id}/report")]
    public async Task<ActionResult> GetReportById(int id)
    {
        var closure = await _dailyClosureService.GetClosureAsync(id);
        decimal exchangeRate = await GetTodayExchangeRateAsync();

        if (closure == null)
        {
            return await GetCurrentReport();
        }

        var details = closure.Details.Select(d =>
        {
            bool isUsd = d.PaymentMethodName.ToLower().Contains("usd") || d.PaymentMethodName.ToLower().Contains("dolar") || d.PaymentMethodName.Contains("$");
            string currency = isUsd ? "USD" : "Bs.S";
            decimal systemAmt = isUsd ? (exchangeRate > 0 ? d.ExpectedAmountBsS / exchangeRate : 0m) : d.ExpectedAmountBsS;
            decimal declaredAmt = isUsd ? (exchangeRate > 0 ? d.ActualAmountBsS / exchangeRate : 0m) : d.ActualAmountBsS;
            decimal diff = declaredAmt - systemAmt;
            string status = Math.Abs(diff) < 0.05m ? "Balanced" : (diff > 0 ? "Surplus" : "Shortage");

            return new ShiftReportDetailDto
            {
                PaymentMethodId = d.PaymentMethodId,
                PaymentMethodName = d.PaymentMethodName,
                Currency = currency,
                DeclaredAmount = declaredAmt,
                SystemAmount = systemAmt,
                Difference = diff,
                Status = status
            };
        }).ToList();

        return Ok(new ShiftReportDto
        {
            ShiftId = closure.Id,
            CashierName = closure.UserId ?? "Cajero Activo",
            CashierCedula = closure.Observation ?? "V-00000000",
            ClosedAt = closure.ClosureDate,
            ExchangeRate = exchangeRate,
            Details = details
        });
    }
}

public class CloseShiftRequest
{
    public string? CashierName { get; set; }
    public string? CashierCedula { get; set; }
    public List<DeclaredAmountDto> DeclaredAmounts { get; set; } = new();
}

public class DeclaredAmountDto
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "Bs.S";
}

public class ShiftReportDto
{
    public int ShiftId { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public string CashierCedula { get; set; } = string.Empty;
    public DateTime ClosedAt { get; set; }
    public decimal ExchangeRate { get; set; }
    public List<ShiftReportDetailDto> Details { get; set; } = new();
}

public class ShiftReportDetailDto
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public string Currency { get; set; } = "Bs.S";
    public decimal DeclaredAmount { get; set; }
    public decimal SystemAmount { get; set; }
    public decimal Difference { get; set; }
    public string Status { get; set; } = "Balanced";
}
