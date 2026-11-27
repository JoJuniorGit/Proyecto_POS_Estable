using Backend.API.Attributes;
using Backend.API.Contracts;
using Core.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

/// <summary>
/// Paso 15 (S5/S8): API del informe de rotacion y del puente de stock muerto al Paso 9. Lectura
/// exclusiva para Admin/Manager; el cajero no ve costos ni capital inmovilizado.
/// </summary>
[ApiController]
[Authorize(Policy = "RequireAdminOrManager")]
[RequireSecurityStampValidation]
[Route("api/inventory-rotation")]
public sealed class InventoryRotationController : ControllerBase
{
    private readonly IInventoryRotationService _rotationService;

    public InventoryRotationController(IInventoryRotationService rotationService)
    {
        ArgumentNullException.ThrowIfNull(rotationService);
        _rotationService = rotationService;
    }

    [HttpGet("report")]
    public async Task<ActionResult<InventoryRotationReportDto>> GetReportAsync(
        [FromQuery] InventoryRotationFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await _rotationService.GetReportAsync(filter, cancellationToken);
            return Ok(report);
        }
        catch (ArgumentException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
    }

    [HttpGet("dead-stock-ids")]
    public async Task<ActionResult<DeadStockSelectionDto>> GetDeadStockProductIdsAsync(
        [FromQuery] InventoryRotationFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var selection = await _rotationService.GetDeadStockProductIdsAsync(filter, cancellationToken);
            return Ok(selection);
        }
        catch (ArgumentException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
    }
}
