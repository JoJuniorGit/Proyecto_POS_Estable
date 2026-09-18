using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System.Threading.Tasks;

using Core.Interfaces;
using Backend.API.Attributes;
using Inventory.Module.Data;

namespace Backend.API.Controllers;

[Authorize]

[ApiController]
[Route("api/[controller]")]
public class CashDrawerController : ControllerBase
{
    private readonly ICashDrawerService _cashDrawerService;
    private readonly ISystemSettingsService _settingsService;
    private readonly Sales.Module.Data.SalesDbContext _db;
    private readonly ICurrentUserService _currentUserService;
    private readonly InventoryDbContext _inventoryContext;
    private readonly Sales.Module.Services.CashAdvanceCoordinator _cashAdvanceCoordinator;

    public CashDrawerController(
        ICashDrawerService cashDrawerService, 
        ISystemSettingsService settingsService, 
        Sales.Module.Data.SalesDbContext db,
        ICurrentUserService currentUserService,
        InventoryDbContext inventoryContext,
        Sales.Module.Services.CashAdvanceCoordinator cashAdvanceCoordinator)
    {
        _cashDrawerService = cashDrawerService;
        _settingsService = settingsService;
        _db = db;
        _currentUserService = currentUserService;
        _inventoryContext = inventoryContext;
        _cashAdvanceCoordinator = cashAdvanceCoordinator;
    }

    [HttpGet("active-session")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<CashDrawerSessionResponseDto?>> GetActiveSession(CancellationToken cancellationToken)
    {
        var session = await _cashDrawerService.GetActiveSessionWithTransactionsAsync(cancellationToken);
        if (session == null)
        {
            return Ok(null);
        }

        return Ok(await MapLocalTimesAsync(session, cancellationToken));
    }

    [HttpGet("history")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<IEnumerable<CashTransactionResponseDto>>> GetHistory([FromQuery] int limit = 300, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 300);
        var transactions = await _cashDrawerService.GetHistoryAsync(limit, cancellationToken);

        var tzId = await _settingsService.GetSettingAsync("SelectedTimeZoneId");
        var tz = Core.Helpers.TimeZoneHelper.GetTimeZone(tzId);

        return Ok(transactions.Select(tx => MapLocalTime(tx, tz)));
    }

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("open")]
    [HttpPost("open-session")]
    public async Task<ActionResult<CashDrawerSessionResponseDto>> OpenSession([FromBody] OpenSessionRequest request, CancellationToken cancellationToken)
    {
        var session = await _cashDrawerService.OpenSessionAsync(request.OpeningBalanceLocal, request.CurrentExchangeRate, cancellationToken);
        return Ok(await MapLocalTimesAsync(session, cancellationToken));
    }

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("close")]
    public async Task<ActionResult<CashDrawerSessionResponseDto>> CloseSession([FromBody] CloseSessionRequest request, CancellationToken cancellationToken)
    {
        var session = await _cashDrawerService.CloseSessionAsync(request.ActualClosingBalanceLocal, request.CurrentExchangeRate, cancellationToken);
        return Ok(await MapLocalTimesAsync(session, cancellationToken));
    }

    [HttpGet("current-balance")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<decimal>> GetCurrentBalance([FromQuery] int sessionId, CancellationToken cancellationToken)
    {
        var balance = await _cashDrawerService.GetCurrentBalanceLocalAsync(sessionId, cancellationToken);
        return Ok(balance);
    }

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager")]
    [HttpPost("transaction")]
    public async Task<ActionResult<CashTransactionResponseDto>> AddTransaction([FromBody] AddTransactionRequest request, CancellationToken cancellationToken)
    {
        // 8.5-A4: Los orígenes Opening y Closing están reservados al ciclo interno de apertura/cierre
        // y NO deben aceptarse desde el endpoint manual (evita bypass del chequeo de saldo).
        if (request.Source == CashTransactionSource.Closing || request.Source == CashTransactionSource.Opening)
        {
            return this.ApiBadRequest("Acceso denegado: los orígenes Opening y Closing están reservados al proceso interno de apertura y cierre de caja y no pueden usarse en transacciones manuales.");
        }

        if (request.Source == CashTransactionSource.CashIn || request.Source == CashTransactionSource.CashOut || request.Source == CashTransactionSource.ManualAdjustment)
        {
            if (_currentUserService.UserRole.HasValue && 
                (_currentUserService.UserRole.Value == Core.Entities.UserRole.Cashier || _currentUserService.UserRole.Value == Core.Entities.UserRole.Driver))
            {
                return this.ApiForbidden("Acceso denegado: Únicamente los usuarios administradores pueden realizar operaciones manuales de ingreso (CASH IN) o retiro (CASH OUT) en la caja.");
            }
        }

        if (request.AmountLocal <= 0)
        {
            return this.ApiBadRequest("El monto de la transacción debe ser mayor a cero.");
        }

        if (request.ExchangeRate <= 0)
        {
            return this.ApiBadRequest("La tasa de cambio (ExchangeRate) debe ser mayor a cero.");
        }

        // 8.5-A5 (residual): la transacción manual también ancla la tasa a la BCV del día con la
        // misma política que CompleteSale/HoldSale (desvío > 10% => ancla; >= ±100% => rechazo;
        // fail-open auditable si no hay BCV del día).
        decimal anchoredRate = await ResolveAnchoredRateAsync(request.ExchangeRate, referenceId: request.SessionId, cancellationToken: cancellationToken);

        decimal amountUsd = Math.Round(request.AmountLocal / anchoredRate, 2, MidpointRounding.AwayFromZero);
        
        var transaction = await _cashDrawerService.AddTransactionAsync(
            request.SessionId,
            request.Type,
            request.Source,
            request.AmountLocal,
            amountUsd,
            anchoredRate,
            request.Description,
            null,
            true,
            null,
            cancellationToken);
        var tzId = await _settingsService.GetSettingAsync("SelectedTimeZoneId");
        var tz = Core.Helpers.TimeZoneHelper.GetTimeZone(tzId);
        return Ok(MapLocalTime(transaction, tz));
    }

    /// <summary>
    /// 8.5-A5 (residual): misma política de anclaje BCV que CompleteSale/HoldSale para la
    /// transacción manual de caja. Desvío ≤ tolerancia (default 10%): se acepta la recibida;
    /// desvío > tolerancia: se ancla a la BCV del día; desvío ≥ ±100%: rechazo; sin BCV del día:
    /// fail-open auditable (nunca catch-swallow).
    /// </summary>
    private async Task<decimal> ResolveAnchoredRateAsync(decimal clientRate, int referenceId, CancellationToken cancellationToken)
    {
        if (clientRate <= 0m)
        {
            throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio inválida o no inicializada (<= 0).");
        }

        // 8.103: la tasa que entra a cualquier cálculo es la referencia redondeada (techo 2d).
        clientRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(clientRate);

        decimal officialRate = 0m;
        try
        {
            var record = await _inventoryContext.ExchangeRateHistory
                .Where(r => r.Date <= Core.Helpers.TimeZoneHelper.GetVenezuelaDate())
                .OrderByDescending(r => r.Date)
                .FirstOrDefaultAsync(cancellationToken);
            officialRate = record != null ? Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(record.Rate) : 0m;
        }
        catch (System.Exception ex)
        {
            Core.Logging.AppLogger.LogDbError(ex, "CashDrawerController.ResolveAnchoredRate");
            return clientRate;
        }

        if (officialRate <= 0m)
        {
            Core.Logging.AppLogger.LogWarn($"[A5-AUDIT] Sin tasa BCV del día en CashDrawerController manual txn #{referenceId}. Fail-open: tasa recibida {clientRate}.");
            return clientRate;
        }

        decimal deviationPct = Math.Abs(clientRate - officialRate) / officialRate;
        if (deviationPct >= 1.0m)
        {
            Core.Logging.AppLogger.LogWarn($"[A5-AUDIT] Tasa rechazada por manipulación en txn manual #{referenceId}: recibida={clientRate}, BCV={officialRate}, desvío={deviationPct:P2}");
            throw new ArgumentException($"La tasa de cambio {clientRate} fue rechazada: excede ±100% de la tasa BCV oficial ({officialRate}). Contacte al supervisor.");
        }

        decimal tolerancePct = 0.10m;
        try
        {
            var toleranceSetting = await _settingsService.GetSettingAsync("RateDeviationTolerancePct");
            if (decimal.TryParse(toleranceSetting, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0m && parsed < 1.0m)
            {
                tolerancePct = parsed;
            }
        }
        catch (System.Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[A5-AUDIT] No se pudo leer la tolerancia configurada en txn manual #{referenceId}; se usa default 10%. {ex.Message}");
        }

        if (deviationPct > tolerancePct)
        {
            Core.Logging.AppLogger.LogWarn($"[A5-AUDIT] Desvío de tasa ({deviationPct:P2} > {tolerancePct:P2}) en txn manual #{referenceId}. Se ANCLA: {clientRate} -> {officialRate}");
            return officialRate;
        }

        return clientRate;
    }

    private async Task<CashDrawerSessionResponseDto> MapLocalTimesAsync(CashDrawerSessionResponseDto session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tzId = await _settingsService.GetSettingAsync("SelectedTimeZoneId");
        var tz = Core.Helpers.TimeZoneHelper.GetTimeZone(tzId);

        return session with
        {
            OpenedAtLocal = System.TimeZoneInfo.ConvertTimeFromUtc(session.OpenedAt, tz),
            ClosedAtLocal = session.ClosedAt.HasValue
                ? System.TimeZoneInfo.ConvertTimeFromUtc(session.ClosedAt.Value, tz)
                : null,
            Transactions = session.Transactions.Select(tx => MapLocalTime(tx, tz)).ToList()
        };
    }

    private static CashTransactionResponseDto MapLocalTime(CashTransactionResponseDto transaction, TimeZoneInfo tz)
    {
        string description = transaction.InvoiceNumber.HasValue
            ? $"Factura N° {transaction.InvoiceNumber.Value}"
            : transaction.Description;

        return transaction with
        {
            TransactionTimeLocal = System.TimeZoneInfo.ConvertTimeFromUtc(transaction.TransactionTime, tz),
            Description = description
        };
    }

    [HttpGet("advance-commission")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<CashAdvanceCommissionDto>> GetAdvanceCommission([FromQuery] bool isTransfer, CancellationToken cancellationToken)
    {
        var percentage = await _cashAdvanceCoordinator.TryGetCommissionPercentageAsync(isTransfer, cancellationToken);

        if (!percentage.HasValue)
        {
            return this.ApiUnprocessableEntity("La comisión de adelanto de efectivo no está configurada o es inválida. Configure la comisión del canal en SystemSettings antes de procesar adelantos.");
        }

        return Ok(new CashAdvanceCommissionDto(isTransfer, percentage.Value));
    }

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("cash-advance")]
    public async Task<ActionResult<CashAdvanceResultDto>> ProcessCashAdvance([FromBody] CashAdvanceRequest request, CancellationToken cancellationToken)
    {
        int? cashierId = null;
        if (_currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int parsedAuthId))
        {
            cashierId = parsedAuthId;
        }
        else
        {
            cashierId = request.CashierId;
        }

        string userName = !string.IsNullOrWhiteSpace(request.UserName)
            ? request.UserName
            : (cashierId.HasValue ? (await _db.Users.FindAsync(new object[] { cashierId.Value }, cancellationToken))?.Name ?? "Usuario" : "Usuario");

        var result = await _cashAdvanceCoordinator.ProcessAsync(
            request.SessionId,
            request.RequestedAmountLocal,
            request.PaymentMethodId,
            request.PaymentMethodName,
            request.IsTransfer,
            request.ExchangeRate,
            cashierId,
            userName,
            cancellationToken);

        var tzId = await _settingsService.GetSettingAsync("SelectedTimeZoneId");
        var tz = Core.Helpers.TimeZoneHelper.GetTimeZone(tzId);
        result.ExpenseTransaction = result.ExpenseTransaction with { TransactionTimeLocal = System.TimeZoneInfo.ConvertTimeFromUtc(result.ExpenseTransaction.TransactionTime, tz) };
        result.IncomeTransaction = result.IncomeTransaction with { TransactionTimeLocal = System.TimeZoneInfo.ConvertTimeFromUtc(result.IncomeTransaction.TransactionTime, tz) };

        return Ok(result);
    }
}

public class OpenSessionRequest
{
    public decimal OpeningBalanceLocal { get; set; }
    public decimal CurrentExchangeRate { get; set; }
}

public class CloseSessionRequest
{
    public decimal ActualClosingBalanceLocal { get; set; }
    public decimal CurrentExchangeRate { get; set; }
}

public class AddTransactionRequest
{
    public int SessionId { get; set; }
    public decimal AmountLocal { get; set; }
    public CashTransactionType Type { get; set; }
    public CashTransactionSource Source { get; set; }
    public decimal ExchangeRate { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class CashAdvanceRequest
{
    public int SessionId { get; set; }
    public decimal RequestedAmountLocal { get; set; }
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public bool IsTransfer { get; set; }
    public decimal ExchangeRate { get; set; }
    public int? CashierId { get; set; }
    public string? UserName { get; set; }
}
