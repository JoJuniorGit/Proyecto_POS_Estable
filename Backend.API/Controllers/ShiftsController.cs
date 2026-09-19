using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Core.Interfaces;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.Linq;
using Backend.API.Attributes;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/shifts")]
public class ShiftsController : ControllerBase
{
    private readonly IDailyClosureService _dailyClosureService;
    private readonly ICurrentUserService _currentUserService;

    public ShiftsController(
        IDailyClosureService dailyClosureService,
        ICurrentUserService currentUserService)
    {
        _dailyClosureService = dailyClosureService;
        _currentUserService = currentUserService;
    }

    [RequireSecurityStampValidation]
    [HttpPost("close")]
    public async Task<ActionResult> CloseShiftAsync([FromBody] CloseShiftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.DeclaredAmounts);

        if (User.IsInRole("Driver"))
        {
            return this.ApiForbidden("El rol Driver no tiene permisos para cerrar turnos.");
        }

        var duplicatedMethodIds = request.DeclaredAmounts
            .GroupBy(d => d.PaymentMethodId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicatedMethodIds.Count > 0)
        {
            return this.ApiBadRequest($"El desglose contiene métodos de pago duplicados: {string.Join(", ", duplicatedMethodIds)}.");
        }

        try
        {
            string? userId = _currentUserService.UserId
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.Identity?.Name;

            var command = new CreateClosureCommand(
                ClosureDateUtc: DateTime.UtcNow,
                UserId: userId ?? "Cajero",
                Observation: "",
                Declarations: request.DeclaredAmounts
                    .Select(d => new DeclaredPaymentAmount(d.PaymentMethodId, d.Amount))
                    .ToList());

            var result = await _dailyClosureService.CreateClosureFromCommandAsync(command, cancellationToken);

            var report = new ShiftReportDto
            {
                ShiftId = result.ClosureId,
                CashierName = result.CashierName,
                CashierCedula = result.CashierCedula,
                ClosedAt = result.ClosedAt,
                ExchangeRate = result.ExchangeRate,
                Details = result.Details.Select(d => new ShiftReportDetailDto
                {
                    PaymentMethodId = d.PaymentMethodId,
                    PaymentMethodName = d.PaymentMethodName,
                    Currency = d.Currency,
                    DeclaredAmount = d.DeclaredAmount,
                    SystemAmount = d.SystemAmount,
                    Difference = d.Difference,
                    Status = d.Status
                }).ToList()
            };

            return Ok(report);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (DbUpdateException)
        {
            return this.ApiConflict("Conflicto de concurrencia al registrar el cierre del turno. Es posible que ya se haya ejecutado otro cierre en paralelo.");
        }
    }

    [HttpGet("current/report")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult> GetCurrentReportAsync(CancellationToken cancellationToken)
    {
        var latestClosure = await _dailyClosureService.GetLatestClosureAsync(cancellationToken);

        if (latestClosure != null)
        {
            return await GetReportByIdAsync(latestClosure.Id, cancellationToken);
        }

        return this.ApiNotFound("No existe ningún cierre de caja registrado todavía.");
    }

    [HttpGet("{id}/report")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult> GetReportByIdAsync(int id, CancellationToken cancellationToken)
    {
        var closure = await _dailyClosureService.GetClosureAsync(id, cancellationToken);

        if (closure == null)
        {
            return this.ApiNotFound("El reporte de cierre solicitado no existe.");
        }

        bool isElevated = User.IsInRole(nameof(Core.Entities.UserRole.Admin)) || User.IsInRole(nameof(Core.Entities.UserRole.Manager));
        if (!isElevated)
        {
            var identityId = _currentUserService.UserId ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var identityName = User.Identity?.Name;
            var identityCedula = User.FindFirst(System.Security.Claims.ClaimTypes.SerialNumber)?.Value;
            bool isOwner = (!string.IsNullOrEmpty(closure.UserId) && !string.IsNullOrEmpty(identityId) && closure.UserId == identityId)
                        || (!string.IsNullOrEmpty(closure.UserId) && !string.IsNullOrEmpty(identityName)
                                && string.Equals(closure.UserId, identityName, System.StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrEmpty(closure.Observation) && !string.IsNullOrEmpty(identityCedula)
                                && string.Equals(closure.Observation, identityCedula, System.StringComparison.OrdinalIgnoreCase));

            if (!isOwner)
            {
                return this.ApiForbidden("Acceso denegado: no tiene permisos para consultar este reporte.");
            }
        }

        var details = ShiftReportMapper.MapDetails(closure.Details, closure.ExchangeRate);

        string cashierName = closure.UserId ?? "Cajero Activo";
        if (int.TryParse(closure.UserId, out int parsedId))
        {
            var displayName = await _dailyClosureService.GetCashierDisplayNameAsync(parsedId, cancellationToken);
            if (displayName != null) cashierName = displayName;
        }

        return Ok(new ShiftReportDto
        {
            ShiftId = closure.Id,
            CashierName = cashierName,
            CashierCedula = closure.Observation ?? "V-00000000",
            ClosedAt = closure.ClosureDate,
            ExchangeRate = closure.ExchangeRate,
            Details = details
        });
    }
}

