using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Core.Entities;
using Core.Interfaces;
using Backend.API.DTOs;
using Backend.API.Attributes;
using Inventory.Module.Data;
using System;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[Authorize]
[RequireSecurityStampValidation]
[ApiController]
[Route("api/[controller]")]
public class ReservationsController : ControllerBase
{
    private readonly IInventoryService _inventoryService;
    private readonly InventoryDbContext? _inventoryContext;

    public ReservationsController(IInventoryService inventoryService, InventoryDbContext? inventoryContext = null)
    {
        _inventoryService = inventoryService;
        _inventoryContext = inventoryContext;
    }

    [HttpPost("reserve")]
    public async Task<IActionResult> ReserveStock([FromBody] ReserveStockDto dto)
    {
        if (dto.Quantity <= 0)
        {
            return BadRequest(new { Message = "La cantidad a reservar debe ser mayor a cero." });
        }

        if (dto.Quantity > 1000m)
        {
            return BadRequest(new { Message = "La cantidad máxima permitida por reserva individual es de 1.000 unidades." });
        }

        try
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                         ?? User.Identity?.Name
                         ?? "anonymous";
            var userRef = $"user:{userId}";

            // Verificar tope de reservas activas por usuario para evitar acaparamiento / DoS (8.2-M4)
            if (_inventoryContext != null)
            {
                var activeCount = await _inventoryContext.StockReservations
                    .AsNoTracking()
                    .CountAsync(r => !r.IsConfirmed && r.ExpiryDate > DateTime.UtcNow && r.ReferenceId == userRef);

                if (activeCount >= 10)
                {
                    return BadRequest(new { Message = "Límite de reservas simultáneas alcanzado para este usuario (máximo 10 activas)." });
                }
            }

            var clampedDuration = Math.Clamp(dto.DurationSeconds, 30, 86400); // 30 seconds to 24 hours max
            var reservationId = await _inventoryService.ReserveStockAsync(
                dto.ProductId,
                dto.Quantity,
                TimeSpan.FromSeconds(clampedDuration),
                userRef
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
    }

    [HttpPost("confirm/{id}")]
    public async Task<IActionResult> ConfirmReservation(int id, [FromBody] ConfirmReservationDto dto)
    {
        try
        {
            // 8.5-A3: Verificar ownership — solo el usuario que creó la reserva puede confirmarla
            if (_inventoryContext != null)
            {
                var reservation = await _inventoryContext.StockReservations
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == id);

                if (reservation == null) return NotFound();

                if (!IsReservationOwner(reservation))
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new { Message = "Acceso denegado: no tiene permisos para confirmar esta reserva." });
            }

            await _inventoryService.ConfirmReservationAsync(id, dto.Reason);
            return NoContent();
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { Message = ex.Message });
        }
    }

    [HttpPost("cancel/{id}")]
    public async Task<IActionResult> CancelReservation(int id)
    {
        try
        {
            // 8.5-A3: Verificar ownership — solo el usuario que creó la reserva puede cancelarla
            if (_inventoryContext != null)
            {
                var reservation = await _inventoryContext.StockReservations
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == id);

                if (reservation == null) return NotFound();

                if (!IsReservationOwner(reservation))
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new { Message = "Acceso denegado: no tiene permisos para cancelar esta reserva." });
            }

            await _inventoryService.CancelReservationAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { Message = ex.Message });
        }
    }

    private bool IsReservationOwner(Core.Entities.StockReservation reservation)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? User.Identity?.Name;
        if (string.IsNullOrEmpty(userId)) return false;
        var userRef = $"user:{userId}";
        // 8.7-M11: reservas de sistema (ReferenceId == null) u otras reservas ajenas NO son
        // confirmables/cancelables por un usuario autenticado.
        return reservation.ReferenceId != null && reservation.ReferenceId == userRef;
    }
}
