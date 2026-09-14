using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using Sales.Module.Entities;
using Sales.Module.Exceptions;
using System;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class SalesController
{
    [HttpPost("{id}/claim")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<SaleDto>> ClaimSale(int id, [FromBody] ClaimSaleRequest request)
    {
        if (request == null)
        {
            return BadRequest(new { message = "La acción debe ser 'Editing' o 'Checkout'." });
        }

        var actionValue = request.Action?.Trim();
        SaleClaimAction action;
        if (string.Equals(actionValue, nameof(SaleClaimAction.Editing), StringComparison.OrdinalIgnoreCase))
        {
            action = SaleClaimAction.Editing;
        }
        else if (string.Equals(actionValue, nameof(SaleClaimAction.Checkout), StringComparison.OrdinalIgnoreCase))
        {
            action = SaleClaimAction.Checkout;
        }
        else
        {
            return BadRequest(new { message = "La acción debe ser 'Editing' o 'Checkout'." });
        }

        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para retomar esta venta." });
        }

        try
        {
            var sale = await _salesService.ClaimSaleAsync(id, action, GetActorUserId());
            return Ok(sale);
        }
        catch (SaleLockedException ex)
        {
            return Conflict(new
            {
                message = ex.Message,
                claimedByUserId = ex.ClaimedByUserId,
                claimedByUserName = ex.ClaimedByUserName,
                claimAction = ex.ClaimAction,
                claimedAtUtc = ex.ClaimedAtUtc
            });
        }
    }

    [HttpPost("{id}/release")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<SaleDto>> ReleaseSale(int id, [FromQuery] bool force = false)
    {
        if (force && !User.IsInRole("Admin") && !User.IsInRole("Manager"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Solo Administradores o Supervisores pueden liberar el bloqueo de otro cajero." });
        }

        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para liberar esta venta." });
        }

        var sale = await _salesService.ReleaseSaleAsync(id, GetActorUserId(), force);
        return Ok(sale);
    }
}

public class ClaimSaleRequest
{
    public string Action { get; set; } = "Editing";
}
