using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Interfaces;
using Backend.API.Attributes;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class CashDrawerController : ControllerBase
{
    private readonly ICashDrawerService _cashDrawerService;
    private readonly ISystemSettingsService _settingsService;
    private readonly IInventoryService _inventoryService;
    private readonly IUserService _userService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ITimeZoneProvider _timeZoneProvider;
    private readonly Sales.Module.Services.CashAdvanceCoordinator _cashAdvanceCoordinator;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public CashDrawerController(
        ICashDrawerService cashDrawerService, 
        ISystemSettingsService settingsService, 
        IInventoryService inventoryService,
        IUserService userService,
        ICurrentUserService currentUserService,
        ITimeZoneProvider timeZoneProvider,
        Sales.Module.Services.CashAdvanceCoordinator cashAdvanceCoordinator)
    {
        _cashDrawerService = cashDrawerService;
        _settingsService = settingsService;
        _inventoryService = inventoryService;
        _userService = userService;
        _currentUserService = currentUserService;
        _timeZoneProvider = timeZoneProvider;
        _cashAdvanceCoordinator = cashAdvanceCoordinator;
    }

    public CashDrawerController(
        ICashDrawerService cashDrawerService, 
        ISystemSettingsService settingsService, 
        Sales.Module.Data.SalesDbContext db,
        ICurrentUserService currentUserService,
        Inventory.Module.Data.InventoryDbContext inventoryContext,
        Sales.Module.Services.CashAdvanceCoordinator cashAdvanceCoordinator)
        : this(
            cashDrawerService,
            settingsService,
            new Inventory.Module.Services.InventoryService(inventoryContext),
            new Sales.Module.Services.UserService(db),
            currentUserService,
            new Core.Services.TimeZoneProvider(settingsService),
            cashAdvanceCoordinator)
    {
    }

    [HttpGet("active-session")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<CashDrawerSessionResponseDto?>> GetActiveSessionAsync(CancellationToken cancellationToken)
    {
        var session = await _cashDrawerService.GetActiveSessionWithTransactionsAsync(cancellationToken);
        if (session == null)
        {
            return Ok(null);
        }

        return Ok(await MapLocalTimesAsync(session, cancellationToken));
    }

    [NonAction]
    public Task<ActionResult<CashDrawerSessionResponseDto?>> GetActiveSession(CancellationToken cancellationToken) => GetActiveSessionAsync(cancellationToken);

    [HttpGet("history")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<IEnumerable<CashTransactionResponseDto>>> GetHistoryAsync([FromQuery] int limit = 300, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 300);
        var transactions = await _cashDrawerService.GetHistoryAsync(limit, cancellationToken);
        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);

        return Ok(transactions.Select(tx => MapLocalTime(tx, tz)));
    }

    [NonAction]
    public Task<ActionResult<IEnumerable<CashTransactionResponseDto>>> GetHistory(int limit = 300, CancellationToken cancellationToken = default) => GetHistoryAsync(limit, cancellationToken);

    [HttpGet("history/paged")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<PagedResultDto<CashTransactionResponseDto>>> GetHistoryPagedAsync([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var paged = await _cashDrawerService.GetHistoryPagedAsync(page, pageSize, cancellationToken);
        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);

        var mappedItems = paged.Items.Select(tx => MapLocalTime(tx, tz)).ToList();
        return Ok(new PagedResultDto<CashTransactionResponseDto>(mappedItems, paged.TotalCount, paged.HasMore));
    }

    [NonAction]
    public Task<ActionResult<PagedResultDto<CashTransactionResponseDto>>> GetHistoryPaged(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => GetHistoryPagedAsync(page, pageSize, cancellationToken);

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("open")]
    [HttpPost("open-session")]
    public async Task<ActionResult<CashDrawerSessionResponseDto>> OpenSessionAsync([FromBody] OpenSessionRequest request, CancellationToken cancellationToken)
    {
        var session = await _cashDrawerService.OpenSessionAsync(request.OpeningBalanceLocal, request.CurrentExchangeRate, cancellationToken);
        return Ok(await MapLocalTimesAsync(session, cancellationToken));
    }

    [NonAction]
    public Task<ActionResult<CashDrawerSessionResponseDto>> OpenSession(OpenSessionRequest request, CancellationToken cancellationToken) => OpenSessionAsync(request, cancellationToken);

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("close")]
    public async Task<ActionResult<CashDrawerSessionResponseDto>> CloseSessionAsync([FromBody] CloseSessionRequest request, CancellationToken cancellationToken)
    {
        var session = await _cashDrawerService.CloseSessionAsync(request.ActualClosingBalanceLocal, request.CurrentExchangeRate, cancellationToken);
        return Ok(await MapLocalTimesAsync(session, cancellationToken));
    }

    [NonAction]
    public Task<ActionResult<CashDrawerSessionResponseDto>> CloseSession(CloseSessionRequest request, CancellationToken cancellationToken) => CloseSessionAsync(request, cancellationToken);

    [HttpGet("current-balance")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<decimal>> GetCurrentBalanceAsync([FromQuery] int sessionId, CancellationToken cancellationToken)
    {
        var balance = await _cashDrawerService.GetCurrentBalanceLocalAsync(sessionId, cancellationToken);
        return Ok(balance);
    }

    [NonAction]
    public Task<ActionResult<decimal>> GetCurrentBalance(int sessionId, CancellationToken cancellationToken) => GetCurrentBalanceAsync(sessionId, cancellationToken);

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager")]
    [HttpPost("transaction")]
    public async Task<ActionResult<CashTransactionResponseDto>> AddTransactionAsync([FromBody] AddTransactionRequest request, CancellationToken cancellationToken)
    {
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

        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);
        return Ok(MapLocalTime(transaction, tz));
    }

    [NonAction]
    public Task<ActionResult<CashTransactionResponseDto>> AddTransaction(AddTransactionRequest request, CancellationToken cancellationToken) => AddTransactionAsync(request, cancellationToken);

    private async Task<decimal> ResolveAnchoredRateAsync(decimal clientRate, int referenceId, CancellationToken cancellationToken)
    {
        if (clientRate <= 0m)
        {
            throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio inválida o no inicializada (<= 0).");
        }

        clientRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(clientRate);

        decimal officialRate = 0m;
        try
        {
            officialRate = await _inventoryService.GetTodayExchangeRateAsync(cancellationToken);
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
        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);

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
    public async Task<ActionResult<CashAdvanceCommissionDto>> GetAdvanceCommissionAsync([FromQuery] bool isTransfer, CancellationToken cancellationToken)
    {
        var percentage = await _cashAdvanceCoordinator.TryGetCommissionPercentageAsync(isTransfer, cancellationToken);

        if (!percentage.HasValue)
        {
            return this.ApiUnprocessableEntity("La comisión de adelanto de efectivo no está configurada o es inválida. Configure la comisión del canal en SystemSettings antes de procesar adelantos.");
        }

        return Ok(new CashAdvanceCommissionDto(isTransfer, percentage.Value));
    }

    [NonAction]
    [HttpGet("advance-commission")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public Task<ActionResult<CashAdvanceCommissionDto>> GetAdvanceCommission(bool isTransfer, CancellationToken cancellationToken) => GetAdvanceCommissionAsync(isTransfer, cancellationToken);

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("cash-advance")]
    public async Task<ActionResult<CashAdvanceResultDto>> ProcessCashAdvanceAsync([FromBody] CashAdvanceRequest request, CancellationToken cancellationToken)
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
            : (cashierId.HasValue ? (await _userService.GetUserNameByIdAsync(cashierId.Value, cancellationToken)) ?? "Usuario" : "Usuario");

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

        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);
        result.ExpenseTransaction = result.ExpenseTransaction with { TransactionTimeLocal = System.TimeZoneInfo.ConvertTimeFromUtc(result.ExpenseTransaction.TransactionTime, tz) };
        result.IncomeTransaction = result.IncomeTransaction with { TransactionTimeLocal = System.TimeZoneInfo.ConvertTimeFromUtc(result.IncomeTransaction.TransactionTime, tz) };

        return Ok(result);
    }

    [NonAction]
    [HttpPost("cash-advance")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public Task<ActionResult<CashAdvanceResultDto>> ProcessCashAdvance(CashAdvanceRequest request, CancellationToken cancellationToken) => ProcessCashAdvanceAsync(request, cancellationToken);
}
