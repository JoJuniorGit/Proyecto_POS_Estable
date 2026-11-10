using Backend.API.Attributes;
using Backend.API.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

/// <summary>
/// Paso 14 (F3-S1): API REST del Estado de Resultados. Lectura exclusiva para Admin/Manager;
/// el cajero y los no autenticados quedan fuera del reporte de rentabilidad.
/// </summary>
[ApiController]
[Authorize(Policy = "RequireAdminOrManager")]
[RequireSecurityStampValidation]
[Route("api/profitability")]
public sealed class ProfitabilityController : ControllerBase
{
    private readonly IProfitabilityService _profitabilityService;

    public ProfitabilityController(IProfitabilityService profitabilityService)
    {
        ArgumentNullException.ThrowIfNull(profitabilityService);
        _profitabilityService = profitabilityService;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<ProfitabilitySummaryDto>> GetSummaryAsync(
        [FromQuery] ProfitabilityFilterRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var summary = await _profitabilityService.GetSummaryAsync(
                request.ToFilter(),
                cancellationToken);
            return Ok(summary);
        }
        catch (ArgumentException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
    }

    [HttpGet("breakdown")]
    public async Task<ActionResult<ProfitabilityBreakdownDto>> GetBreakdownAsync(
        [FromQuery] ProfitabilityFilterRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var breakdown = await _profitabilityService.GetBreakdownAsync(
                request.ToFilter(),
                cancellationToken);
            return Ok(breakdown);
        }
        catch (ArgumentException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
    }
}
