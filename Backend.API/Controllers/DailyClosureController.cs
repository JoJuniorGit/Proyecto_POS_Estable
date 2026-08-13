using Microsoft.AspNetCore.Mvc;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DailyClosureController : ControllerBase
{
    private readonly IDailyClosureService _closureService;

    public DailyClosureController(IDailyClosureService closureService)
    {
        _closureService = closureService;
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

            var result = await _closureService.CreateClosureAsync(closure);
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
