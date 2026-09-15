using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.Interfaces;
using Sales.Module.Data;
using Sales.Module.Entities;
using Inventory.Module.Data;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.Linq;
using Backend.API.Attributes;
using Backend.API.Services;

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
    public async Task<ActionResult> CloseShift([FromBody] CloseShiftRequest request)
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

        try
        {
            // 8.9-B4: el cierre de turno combina SalesDbContext (cierre/totales) + caja (rollover) en
            // una transacción Serializable cross-DB; se ejecuta bajo execution strategy para
            // reintentar el bloque completo ante fallos transitorios y no quedar a medias.
            return await _salesContext.Database.CreateExecutionStrategy().ExecuteAsync<ActionResult>(async () =>
            {
            decimal exchangeRate = await GetTodayExchangeRateAsync();

            // 8.2-M2: Bloquear cierre sin tasa BCV del día (o tasa 0/NA explícita) con error claro
            if (exchangeRate <= 0)
            {
                throw new InvalidOperationException("No se puede cerrar el turno: no existe una tasa BCV registrada para hoy. Registre la tasa del día antes de cerrar la caja.");
            }

            using var dbTransaction = await _salesContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            // Obtenemos los totales teóricos por método de pago dentro de la transacción Serializable
            var expectedTotals = await _dailyClosureService.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);

            var expectedMethodIds = expectedTotals.Select(e => e.PaymentMethodId).ToHashSet();
            var unknownMethodIds = request.DeclaredAmounts
                .Where(d => !expectedMethodIds.Contains(d.PaymentMethodId))
                .Select(d => d.PaymentMethodId)
                .Distinct()
                .ToList();

            if (unknownMethodIds.Count > 0)
            {
                return BadRequest(new { message = $"El desglose contiene métodos de pago no reconocidos: {string.Join(", ", unknownMethodIds)}." });
            }

            var details = new List<ShiftReportDetailDto>();
            foreach (var declared in request.DeclaredAmounts)
            {
                var expected = expectedTotals.FirstOrDefault(e => e.PaymentMethodId == declared.PaymentMethodId);
                decimal expectedSystemAmount = 0m;

                if (expected != null)
                {
                    if (declared.Currency == "USD")
                    {
                        expectedSystemAmount = exchangeRate > 0 ? (expected.ExpectedAmountBsS / exchangeRate) : 0m;
                    }
                    else
                    {
                        expectedSystemAmount = expected.ExpectedAmountBsS;
                    }
                }

                decimal diff = declared.Amount - expectedSystemAmount;
                string status = Math.Abs(diff) < 0.05m ? "Balanced" : (diff > 0 ? "Surplus" : "Shortage");

                details.Add(new ShiftReportDetailDto
                {
                    PaymentMethodId = declared.PaymentMethodId,
                    PaymentMethodName = declared.PaymentMethodName,
                    Currency = declared.Currency,
                    DeclaredAmount = declared.Amount,
                    SystemAmount = expectedSystemAmount,
                    Difference = diff,
                    Status = status
                });
            }

            // Identidad autoritativa de cajero por claims (H-API-2)
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
                // Fallback safe defaults if no user is found
                cashierName = "Cajero Desconocido";
                cashierCedula = "V-00000000";
            }

            // Persistir cierre de caja de forma secuencial en la Base de Datos
            var dailyClosure = new DailyClosure
            {
                ClosureDate = DateTime.UtcNow,
                UserId = parsedAuthUserId?.ToString() ?? cashierName,
                Observation = cashierCedula,
                ExchangeRate = exchangeRate,
                Details = details.Select(d => new ClosureDetail
                {
                    PaymentMethodId = d.PaymentMethodId,
                    PaymentMethodName = d.PaymentMethodName,
                    ExpectedAmountBsS = d.Currency == "USD" ? d.SystemAmount * exchangeRate : d.SystemAmount,
                    ActualAmountBsS = d.Currency == "USD" ? d.DeclaredAmount * exchangeRate : d.DeclaredAmount,
                    DifferenceBsS = d.Currency == "USD" ? d.Difference * exchangeRate : d.Difference
                }).ToList()
            };

            var savedClosure = await _dailyClosureService.CreateClosureAsync(dailyClosure);

            // Unificar con DailyClosure: rotar sesión de caja en el cierre de turno (H-API-15)
            await _cashDrawerService.RolloverSessionAfterClosureAsync(exchangeRate);

            await dbTransaction.CommitAsync();

            // 8.7-B5: los comprobantes (PDF/TXT) se escriben DESPUÉS del commit para no mantener
            // abierta la transacción Serializable durante I/O de disco.
            _dailyClosureService.WriteClosedClosureReceipts(savedClosure);

            var report = new ShiftReportDto
            {
                ShiftId = savedClosure.Id,
                CashierName = cashierName,
                CashierCedula = cashierCedula,
                ClosedAt = savedClosure.ClosureDate,
                ExchangeRate = exchangeRate,
                Details = details
            };

            return Ok(report);
            });
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
        // 8.6-B1/8.5-A3 + 8.7-B3: ownership a nivel de objeto — un cajero solo puede ver
        // reportes de sus propios cierres. El cierre persiste UserId (nombre) y Observation
        // (cédula): se compara contra los claims Name/SerialNumber del JWT (no contra el id
        // numérico, que nunca coincide con un nombre almacenado).
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
            // 8.2-B4: sin recursión a GetCurrentReport — un id inexistente no debe fabricar
            // ni devolver el cierre más reciente (semántica de recurso).
            return NotFound(new { Message = "El reporte de cierre solicitado no existe." });
        }

        var details = closure.Details.Select(d =>
        {
            bool isUsd = d.PaymentMethodName.ToLower().Contains("usd") || d.PaymentMethodName.ToLower().Contains("dolar") || d.PaymentMethodName.Contains("$");
            string currency = isUsd ? "USD" : "Bs.S";
            decimal systemAmt = isUsd ? (exchangeRate > 0 ? d.ExpectedAmountBsS / exchangeRate : 0m) : d.ExpectedAmountBsS;
            decimal declaredAmt = isUsd ? (exchangeRate > 0 ? d.ActualAmountBsS / exchangeRate : 0m) : d.ActualAmountBsS;
            decimal diff = declaredAmt - systemAmt;
            string status = Math.Abs(diff) < 0.05m ? "Balanced" : (diff > 0 ? "Surplus" : "Shortage");

            return new ShiftReportDetailDto
            {
                PaymentMethodId = d.PaymentMethodId,
                PaymentMethodName = d.PaymentMethodName,
                Currency = currency,
                DeclaredAmount = declaredAmt,
                SystemAmount = systemAmt,
                Difference = diff,
                Status = status
            };
        }).ToList();

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
    public string Currency { get; set; } = "Bs.S";
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

public class ShiftReportDetailDto
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public string Currency { get; set; } = "Bs.S";
    public decimal DeclaredAmount { get; set; }
    public decimal SystemAmount { get; set; }
    public decimal Difference { get; set; }
    public string Status { get; set; } = "Balanced";
}
