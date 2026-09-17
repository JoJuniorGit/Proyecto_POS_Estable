using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.Interfaces;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Inventory.Module.Data;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.Linq;
using Backend.API.Attributes;
using Backend.API.Services;
using Core.Helpers;
using Sales.Module;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/shifts")]
public class ShiftsController : ControllerBase
{
    private readonly ICashDrawerService _cashDrawerService;
    private readonly IDailyClosureService _dailyClosureService;
    private readonly IPaymentMethodService _paymentMethodService;
    private readonly ISystemSettingsService _settingsService;
    private readonly InventoryDbContext _inventoryContext;
    private readonly SalesDbContext _salesContext;
    private readonly ICurrentUserService _currentUserService;

    public ShiftsController(
        ICashDrawerService cashDrawerService,
        IDailyClosureService dailyClosureService,
        IPaymentMethodService paymentMethodService,
        ISystemSettingsService settingsService,
        InventoryDbContext inventoryContext,
        SalesDbContext salesContext,
        ICurrentUserService currentUserService)
    {
        _cashDrawerService = cashDrawerService;
        _dailyClosureService = dailyClosureService;
        _paymentMethodService = paymentMethodService;
        _settingsService = settingsService;
        _inventoryContext = inventoryContext;
        _salesContext = salesContext;
        _currentUserService = currentUserService;
    }

    // 8.7-M3: tasa efectiva del día centralizada en ExchangeRateResolver (BCV hoy -> histórico -> apertura de sesión).
    private Task<decimal> GetTodayExchangeRateAsync()
    {
        return ExchangeRateResolver.ReadEffectiveTodayRateAsync(_inventoryContext, _cashDrawerService);
    }

    [RequireSecurityStampValidation]
    [HttpPost("close")]
    public async Task<ActionResult> CloseShift([FromBody] CloseShiftRequest request, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Driver"))
        {
            return Forbid();
        }

        var duplicatedMethodIds = request.DeclaredAmounts
            .GroupBy(d => d.PaymentMethodId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicatedMethodIds.Count > 0)
        {
            return BadRequest(new { message = $"El desglose contiene métodos de pago duplicados: {string.Join(", ", duplicatedMethodIds)}." });
        }

        // Identity from JWT claims (H-API-2)
        string cashierName = "Cajero Activo";
        string cashierCedula = "V-00000000";

        int? parsedAuthUserId = null;
        if (_currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int authUserId))
        {
            parsedAuthUserId = authUserId;
            var authUser = await _salesContext.Users.FindAsync(authUserId);
            if (authUser != null)
            {
                cashierName = authUser.Name;
                cashierCedula = authUser.Cedula ?? authUser.Username ?? "V-00000000";
            }
        }
        else if (!string.IsNullOrWhiteSpace(User.Identity?.Name))
        {
            var authUser = await _salesContext.Users.FirstOrDefaultAsync(u => u.Username == User.Identity.Name);
            if (authUser != null)
            {
                parsedAuthUserId = authUser.Id;
                cashierName = authUser.Name;
                cashierCedula = authUser.Cedula ?? authUser.Username ?? "V-00000000";
            }
        }
        else
        {
            cashierName = "Cajero Desconocido";
            cashierCedula = "V-00000000";
        }

        try
        {
            // AD-1/5: build command from declarations; delegate to service.
            // AD-1: no request.Currency reads — classification happens in the service via resolver.
            var command = new CreateClosureCommand(
                ClosureDateUtc: DateTime.UtcNow,
                UserId: parsedAuthUserId?.ToString() ?? cashierName,
                Observation: cashierCedula,
                Declarations: request.DeclaredAmounts
                    .Select(d => new DeclaredPaymentAmount(d.PaymentMethodId, d.Amount))
                    .ToList());

            var result = await _dailyClosureService.CreateClosureFromCommandAsync(command, cancellationToken);

            // Build report from service result (uses resolver classification)
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

            // Unificar con DailyClosure: rotar sesión de caja en el cierre de turno (H-API-15)
            await _cashDrawerService.RolloverSessionAfterClosureAsync(result.ExchangeRate);

            // 8.7-B5: los comprobantes (PDF/TXT) se escriben DESPUÉS del commit
            // (WriteClosedClosureReceipts is fail-open, post-commit per IDailyClosureService contract)

            return Ok(report);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(Problem(ex.Message));
        }
        catch (DbUpdateException)
        {
            return this.ApiConflict("Conflicto de concurrencia al cerrar el turno. Ya se encuentra un cierre en ejecución.");
        }
    }

    [HttpGet("current/report")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult> GetCurrentReport()
    {
        var latestClosure = await _salesContext.DailyClosures
            .Include(c => c.Details)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync();

        if (latestClosure != null)
        {
            return await GetReportById(latestClosure.Id);
        }

        // 8.2-B4/8B-B5: NO fabricar un cierre sintético (ShiftId=1, "Cajero Activo",
        // Difference=-esperado) cuando aún no existe ningún cierre real — un reporte falso
        // distorsionaría arqueos y la recuperación de reportes. Se responde 404 con mensaje
        // explícito para que el cliente lo muestre como "aún no hay cierres".
        return NotFound(new { Message = "No existe ningún cierre de caja registrado todavía." });
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
                return StatusCode(StatusCodes.Status403Forbidden, new { Message = "Acceso denegado: no tiene permisos para consultar este reporte." });
            }
        }

        decimal exchangeRate = closure != null && closure.ExchangeRate > 0 
            ? closure.ExchangeRate 
            : await GetTodayExchangeRateAsync();

        if (closure == null)
        {
            return NotFound(new { Message = "El reporte de cierre solicitado no existe." });
        }

        // AD-4: single projection via ShiftReportMapper (report ↔ receipt agreement by construction)
        var details = ShiftReportMapper.MapDetails(closure.Details, exchangeRate);

        string cashierName = closure.UserId ?? "Cajero Activo";
        if (int.TryParse(closure.UserId, out int parsedId))
        {
            var u = await _salesContext.Users.FindAsync(parsedId);
            if (u != null) cashierName = u.Name;
        }

        return Ok(new ShiftReportDto
        {
            ShiftId = closure.Id,
            CashierName = cashierName,
            CashierCedula = closure.Observation ?? "V-00000000",
            ClosedAt = closure.ClosureDate,
            ExchangeRate = exchangeRate,
            Details = details
        });
    }
}

public class CloseShiftRequest
{
    public string? CashierName { get; set; }
    public string? CashierCedula { get; set; }
    public List<DeclaredAmountDto> DeclaredAmounts { get; set; } = new();
}

public class DeclaredAmountDto
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class ShiftReportDto
{
    public int ShiftId { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public string CashierCedula { get; set; } = string.Empty;
    public DateTime ClosedAt { get; set; }
    public decimal ExchangeRate { get; set; }
    public List<Sales.Module.Services.ShiftReportDetailDto> Details { get; set; } = new();
}
