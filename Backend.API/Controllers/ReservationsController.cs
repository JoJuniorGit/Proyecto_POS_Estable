using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.Interfaces;
using Backend.API.DTOs;
using System;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ReservationsController : ControllerBase
{
    private readonly IInventoryService _inventoryService;

    public ReservationsController(IInventoryService inventoryService)
    {
        _inventoryService = inventoryService;
    }

    [HttpPost("reserve")]
    public async Task<IActionResult> ReserveStock([FromBody] ReserveStockDto dto)
    {
        try
        {
            var reservationId = await _inventoryService.ReserveStockAsync(
                dto.ProductId,
                dto.Quantity,
                TimeSpan.FromSeconds(dto.DurationSeconds)
            );
            return Ok(new { ReservationId = reservationId });
        }
        catch (InvalidOperationException ex)
        {
            // Stock not available or concurrency conflict
            return Conflict(new { Message = ex.Message });
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { Message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpPost("confirm/{id}")]
    public async Task<IActionResult> ConfirmReservation(int id, [FromBody] ConfirmReservationDto dto)
    {
        try
        {
            await _inventoryService.ConfirmReservationAsync(id, dto.Reason);
            return NoContent();
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpPost("cancel/{id}")]
    public async Task<IActionResult> CancelReservation(int id)
    {
        await _inventoryService.CancelReservationAsync(id);
        return NoContent();
    }
}
