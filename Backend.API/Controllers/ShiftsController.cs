using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Interfaces;
using Sales.Module.Entities;
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
    public async Task<ActionResult> CloseShift([FromBody] CloseShiftRequest request, CancellationToken cancellationToken)
    {
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
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DbUpdateException)
        {
            return this.ApiConflict("Conflicto de concurrencia al registrar el cierre del turno. Es posible que ya se haya ejecutado otro cierre en paralelo.");
        }
    }

    [HttpGet("current/report")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult> GetCurrentReport()
    {
        var latestClosure = await _dailyClosureService.GetLatestClosureAsync();

        if (latestClosure != null)
        {
            return await GetReportById(latestClosure.Id);
        }

        return this.ApiNotFound("No existe ningún cierre de caja registrado todavía.");
    }

    [HttpGet("{id}/report")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult> GetReportById(int id)
    {
        var closure = await _dailyClosureService.GetClosureAsync(id);

        bool isElevated = User.IsInRole("Admin") || User.IsInRole("Manager");
        if (!isElevated && closure != null)
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

        if (closure == null)
        {
            return this.ApiNotFound("El reporte de cierre solicitado no existe.");
        }

        var details = ShiftReportMapper.MapDetails(closure.Details, closure.ExchangeRate);

        string cashierName = closure.UserId ?? "Cajero Activo";
        if (int.TryParse(closure.UserId, out int parsedId))
        {
            var displayName = await _dailyClosureService.GetCashierDisplayNameAsync(parsedId);
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

public class CloseShiftRequest
{
    public List<DeclaredAmountDto> DeclaredAmounts { get; set; } = new();
}

public class DeclaredAmountDto
{
    public int PaymentMethodId { get; set; }
    public decimal Amount { get; set; }
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
