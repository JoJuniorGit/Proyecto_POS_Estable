using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Exceptions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class SalesController
{
    [HttpPost("{id}/claim")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<SaleDto>> ClaimSaleAsync(int id, [FromBody] ClaimSaleRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return this.ApiBadRequest("La acción debe ser 'Editing' o 'Checkout'.");
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
            return this.ApiBadRequest("La acción debe ser 'Editing' o 'Checkout'.");
        }

        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para retomar esta venta.");
        }

        try
        {
            var sale = await _salesService.ClaimSaleAsync(id, action, GetActorUserId(), cancellationToken);
            return Ok(sale);
        }
        catch (SaleLockedException ex)
        {
            var pd = new SaleLockedProblemDetails
            {
                Status = Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = ex.Message,
                Type = "https://httpstatuses.com/409",
                Instance = HttpContext?.Request.Path.Value,
                ClaimedByUserId = ex.ClaimedByUserId,
                ClaimedByUserName = ex.ClaimedByUserName,
                ClaimAction = ex.ClaimAction,
                ClaimedAtUtc = ex.ClaimedAtUtc
            };
            pd.Extensions["message"] = ex.Message;
            pd.Extensions["claimedByUserId"] = ex.ClaimedByUserId;
            pd.Extensions["claimedByUserName"] = ex.ClaimedByUserName;
            pd.Extensions["claimAction"] = ex.ClaimAction;
            pd.Extensions["claimedAtUtc"] = ex.ClaimedAtUtc;
            pd.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? HttpContext?.TraceIdentifier;
            return new ConflictObjectResult(pd);
        }
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> ClaimSale(int id, [FromBody] ClaimSaleRequest request) => ClaimSaleAsync(id, request);

    [HttpPost("{id}/release")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<SaleDto>> ReleaseSaleAsync(int id, [FromQuery] bool force = false, CancellationToken cancellationToken = default)
    {
        if (force && !User.IsInRole("Admin") && !User.IsInRole("Manager"))
        {
            return this.ApiForbidden("Solo Administradores o Supervisores pueden liberar el bloqueo de otro cajero.");
        }

        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para liberar esta venta.");
        }

        var sale = await _salesService.ReleaseSaleAsync(id, GetActorUserId(), force, cancellationToken);
        return Ok(sale);
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> ReleaseSale(int id, [FromQuery] bool force = false) => ReleaseSaleAsync(id, force);
}

public class SaleLockedProblemDetails : ProblemDetails
{
    public int? ClaimedByUserId { get; set; }
    public string? ClaimedByUserName { get; set; }
    public string? ClaimAction { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
}
