using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.Interfaces;
using Backend.API.DTOs;
using Backend.API.Attributes;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[Authorize]
[RequireSecurityStampValidation]
[ApiController]
[Route("api/[controller]")]
public class ReservationsController : ControllerBase
{
    private readonly IInventoryService _inventoryService;
    private readonly IReservationService? _reservationService;

    public ReservationsController(IInventoryService inventoryService, IReservationService? reservationService = null)
    {
        _inventoryService = inventoryService;
        _reservationService = reservationService ?? (inventoryService as IReservationService);
    }

    [HttpPost("reserve")]
    public async Task<IActionResult> ReserveStockAsync([FromBody] ReserveStockDto dto, CancellationToken cancellationToken = default)
    {
        if (dto.Quantity <= 0)
        {
            return this.ApiBadRequest("La cantidad a reservar debe ser mayor a cero.");
        }

        if (dto.Quantity > 1000m)
        {
            return this.ApiBadRequest("La cantidad máxima permitida por reserva individual es de 1.000 unidades.");
        }

        try
        {
            var userRef = GetCurrentUserRef();

            var activeCount = _reservationService != null
                ? await _reservationService.GetActiveReservationsCountAsync(userRef, cancellationToken)
                : 0;
            if (activeCount >= 10)
            {
                return this.ApiBadRequest("Límite de reservas simultáneas alcanzado para este usuario (máximo 10 activas).");
            }

            var clampedDuration = Math.Clamp(dto.DurationSeconds, 30, 86400);
            var reservationId = await _inventoryService.ReserveStockAsync(
                dto.ProductId,
                dto.Quantity,
                TimeSpan.FromSeconds(clampedDuration),
                userRef,
                cancellationToken
            );
            return Ok(new ReserveStockResponseDto { ReservationId = reservationId });
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [NonAction]
    public Task<IActionResult> ReserveStock(ReserveStockDto dto) => ReserveStockAsync(dto);

    [HttpPost("confirm/{id}")]
    public async Task<IActionResult> ConfirmReservationAsync(int id, [FromBody] ConfirmReservationDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var userRef = GetCurrentUserRef();
            if (_reservationService != null)
            {
                var ownership = await _reservationService.ValidateReservationOwnershipAsync(id, userRef, cancellationToken);
                if (ownership == null)
                {
                    return this.ApiNotFound("La reserva especificada no existe.");
                }

                if (!ownership.Value)
                {
                    return this.ApiForbidden("Acceso denegado: no tiene permisos para confirmar esta reserva.");
                }
            }

            await _inventoryService.ConfirmReservationAsync(id, dto.Reason, cancellationToken);
            return NoContent();
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return this.ApiNotFound("La reserva especificada no existe.");
        }
    }

    [NonAction]
    public Task<IActionResult> ConfirmReservation(int id, ConfirmReservationDto dto) => ConfirmReservationAsync(id, dto);

    [HttpPost("cancel/{id}")]
    public async Task<IActionResult> CancelReservationAsync(int id, CancellationToken cancellationToken = default)
    {
        var userRef = GetCurrentUserRef();
        if (_reservationService != null)
        {
            var ownership = await _reservationService.ValidateReservationOwnershipAsync(id, userRef, cancellationToken);
            if (ownership == null)
            {
                return this.ApiNotFound("La reserva especificada no existe.");
            }

            if (!ownership.Value)
            {
                return this.ApiForbidden("Acceso denegado: no tiene permisos para cancelar esta reserva.");
            }
        }

        await _inventoryService.CancelReservationAsync(id, cancellationToken);
        return NoContent();
    }

    [NonAction]
    public Task<IActionResult> CancelReservation(int id) => CancelReservationAsync(id);

    private string GetCurrentUserRef()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? User.Identity?.Name
                     ?? "anonymous";
        return $"user:{userId}";
    }
}
