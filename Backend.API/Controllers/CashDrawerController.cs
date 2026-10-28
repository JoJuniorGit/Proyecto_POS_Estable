using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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
using Backend.API.Services;

namespace Backend.API.Controllers;

[RequireSecurityStampValidation]
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
    // 8.149 (SRE-02): clave obligatoria + replay para POST transaction.
    private readonly IdempotencyRequestResolver _idempotencyResolver;
    // 8.205 (C-01): política de anclaje de tasa compartida con SalesService.
    private readonly Sales.Module.Services.ExchangeRateAnchorService _rateAnchor;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public CashDrawerController(
        ICashDrawerService cashDrawerService, 
        ISystemSettingsService settingsService, 
        IInventoryService inventoryService,
        IUserService userService,
        ICurrentUserService currentUserService,
        ITimeZoneProvider timeZoneProvider,
        Sales.Module.Services.CashAdvanceCoordinator cashAdvanceCoordinator,
        IIdempotencyService? idempotencyService = null,
        IMonetaryProfileService? monetaryProfileService = null)
    {
        _cashDrawerService = cashDrawerService;
        _settingsService = settingsService;
        _inventoryService = inventoryService;
        _userService = userService;
        _currentUserService = currentUserService;
        _timeZoneProvider = timeZoneProvider;
        _cashAdvanceCoordinator = cashAdvanceCoordinator;
        _idempotencyResolver = new IdempotencyRequestResolver(idempotencyService, currentUserService);
        // 8.211-T3c3b1 (S4): the injected profile lets Single mode resolve an identity rate.
        _rateAnchor = new Sales.Module.Services.ExchangeRateAnchorService(
            inventoryService, settingsService, monetaryProfileService: monetaryProfileService);
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

        var mapped = await MapLocalTimesAsync(session, cancellationToken);
        if (IsStrictlyCashier())
        {
            mapped = mapped with
            {
                ClosingBalanceLocal = null,
                Transactions = Array.Empty<CashTransactionResponseDto>()
            };
        }

        return Ok(mapped);
    }

    private bool IsStrictlyCashier()
    {
        bool isElevated = User?.IsInRole("Admin") == true
            || User?.IsInRole("Manager") == true
            || _currentUserService.UserRole is Core.Entities.UserRole.Admin or Core.Entities.UserRole.Manager;
        if (isElevated) return false;

        return User?.IsInRole("Cashier") == true
            || _currentUserService.UserRole == Core.Entities.UserRole.Cashier;
    }

    private bool RequireLifecycleIdempotencyKey()
    {
        var configuration = HttpContext?.RequestServices?.GetService(typeof(IConfiguration)) as IConfiguration;
        return configuration?.GetValue<bool>("Idempotency:RequireCashSessionAndInvoiceStage") == true;
    }

    [HttpGet("history")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<IEnumerable<CashTransactionResponseDto>>> GetHistoryAsync([FromQuery] int limit = 300, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 300);
        var transactions = await _cashDrawerService.GetHistoryAsync(limit, cancellationToken);
        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);

        return Ok(transactions.Select(tx => MapLocalTime(tx, tz)));
    }

    [HttpGet("history/paged")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<PagedResultDto<CashTransactionResponseDto>>> GetHistoryPagedAsync([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var paged = await _cashDrawerService.GetHistoryPagedAsync(page, pageSize, cancellationToken);
        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);

        var mappedItems = paged.Items.Select(tx => MapLocalTime(tx, tz)).ToList();
        return Ok(new PagedResultDto<CashTransactionResponseDto>(mappedItems, paged.TotalCount, paged.HasMore));
    }

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("open")]
    [HttpPost("open-session")]
    public async Task<ActionResult<CashDrawerSessionResponseDto>> OpenSessionAsync([FromBody] OpenSessionRequest request, CancellationToken cancellationToken)
    {
        // SRE-10: idempotencia opcional para reintentos de red de la apertura.
        string requestPath = "/api/cashdrawer/open-session";
        string bodyJson = _idempotencyResolver.GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);
        var idempotency = await _idempotencyResolver.ResolveWithPolicyAsync(
            this,
            requestPath,
            bodyJson,
            RequireLifecycleIdempotencyKey());
        if (idempotency.ShouldStop) return idempotency.BlockingResult!;

        // 8.168 (SEC-04-RES): anclar la tasa de apertura al valor oficial BCV cuando existe.
        decimal anchoredRate = await ResolveAnchoredRateAsync(request.CurrentExchangeRate, referenceId: 0, cancellationToken: cancellationToken);
        var session = await _cashDrawerService.OpenSessionAsync(
            request.OpeningBalanceLocal,
            anchoredRate,
            cancellationToken: cancellationToken,
            openingBalanceUsd: request.OpeningBalanceUsd);
        var mapped = await MapLocalTimesAsync(session, cancellationToken);
        await _idempotencyResolver.RegisterSuccessAsync(
            idempotency,
            requestPath,
            System.Text.Json.JsonSerializer.Serialize(mapped),
            cancellationToken);
        return Ok(mapped);
    }

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("close")]
    public async Task<ActionResult<CashDrawerSessionResponseDto>> CloseSessionAsync([FromBody] CloseSessionRequest request, CancellationToken cancellationToken)
    {
        // SRE-10: idempotencia opcional para reintentos de red del cierre.
        string requestPath = "/api/cashdrawer/close";
        string bodyJson = _idempotencyResolver.GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);
        var idempotency = await _idempotencyResolver.ResolveWithPolicyAsync(
            this,
            requestPath,
            bodyJson,
            RequireLifecycleIdempotencyKey());
        if (idempotency.ShouldStop) return idempotency.BlockingResult!;

        // 8.168 (SEC-04-RES): anclar la tasa de cierre al valor oficial BCV cuando existe.
        decimal anchoredRate = await ResolveAnchoredRateAsync(request.CurrentExchangeRate, referenceId: 0, cancellationToken: cancellationToken);
        var session = await _cashDrawerService.CloseSessionAsync(
            request.ActualClosingBalanceLocal,
            anchoredRate,
            actualClosingBalanceUsd: request.ActualClosingBalanceUsd,
            cancellationToken: cancellationToken);
        var mapped = await MapLocalTimesAsync(session, cancellationToken);
        await _idempotencyResolver.RegisterSuccessAsync(
            idempotency,
            requestPath,
            System.Text.Json.JsonSerializer.Serialize(mapped),
            cancellationToken);
        return Ok(mapped);
    }

    [HttpGet("current-balance")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<decimal>> GetCurrentBalanceAsync([FromQuery] int sessionId, CancellationToken cancellationToken)
    {
        var balance = await _cashDrawerService.GetCurrentBalanceLocalAsync(sessionId, cancellationToken);
        return Ok(balance);
    }

    /// <summary>
    /// T4 (Paso 13 / 8.188, S2): saldos físicos del turno por moneda para el panel de caja chica.
    /// Se mantiene bajo el mismo gate de supervisión que el saldo teórico (arqueo ciego).
    /// </summary>
    [HttpGet("physical-balances")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<PettyCashPhysicalBalancesResponseDto>> GetPhysicalBalancesAsync(
        [FromQuery] int sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId <= 0)
        {
            return this.ApiBadRequest("La sesión de caja es requerida para consultar los saldos físicos.");
        }

        decimal bsS = await _cashDrawerService.GetPhysicalBalanceAsync(
            sessionId,
            Sales.Module.Entities.PettyCashCurrency.BsS,
            cancellationToken);
        decimal usd = await _cashDrawerService.GetPhysicalBalanceAsync(
            sessionId,
            Sales.Module.Entities.PettyCashCurrency.Usd,
            cancellationToken);

        return Ok(new PettyCashPhysicalBalancesResponseDto(bsS, usd));
    }

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

        // 8.149 (SRE-02): clave obligatoria + replay/422 antes del anclaje de tasa y del servicio.
        string requestPath = "/api/cashdrawer/transaction";
        string bodyJson = _idempotencyResolver.GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);
        var resolved = await _idempotencyResolver.ResolveAsync(this, requestPath, bodyJson);
        if (resolved.ShouldStop) return resolved.BlockingResult!;

        try
        {
            decimal anchoredRate = await ResolveAnchoredRateAsync(request.ExchangeRate, referenceId: request.SessionId, cancellationToken: cancellationToken);
            decimal amountUsd = Core.Helpers.PricingCalculator.ToUSD(request.AmountLocal, anchoredRate);

            var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);

            // 8.202-H2 (S4): la respuesta idempotente se persiste dentro de la MISMA transacción
            // que el movimiento. El serializador conserva la hora local del contrato HTTP.
            Sales.Module.DTOs.CashTransactionIdempotency? transactionIdempotency =
                resolved.Key is not null && resolved.PayloadHash is not null
                    ? new Sales.Module.DTOs.CashTransactionIdempotency(
                        resolved.Key,
                        requestPath,
                        resolved.PayloadHash,
                        _idempotencyResolver.GetActorUserId(),
                        dto => System.Text.Json.JsonSerializer.Serialize(MapLocalTime(dto, tz)))
                    : null;

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
                cancellationToken,
                transactionIdempotency);

            var mapped = MapLocalTime(transaction, tz);

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            return Ok(mapped);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IdempotencyRequestResolver.IsIdempotencyUniqueViolation(ex))
        {
            return await _idempotencyResolver.HandleCollisionAsync(this, ex, requestPath, resolved.Key, resolved.PayloadHash);
        }
    }

    private async Task<decimal> ResolveAnchoredRateAsync(decimal clientRate, int referenceId, CancellationToken cancellationToken)
    {
        var result = await _rateAnchor.ResolveAsync(clientRate, "CashDrawerController", referenceId, cancellationToken);
        if (result.Outcome == Sales.Module.Services.ExchangeRateAnchorOutcome.Rejected)
        {
            throw new ArgumentException($"La tasa de cambio {result.ClientRate} fue rechazada: excede ±100% de la tasa BCV oficial ({result.OfficialRate}). Contacte al supervisor.");
        }

        return result.EffectiveRate;
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

    /// <summary>
    /// 8.202-H2: aplica la hora local del contrato a ambos movimientos del adelanto. Es idempotente
    /// (siempre recalcula desde <c>TransactionTime</c> UTC), por lo que también se usa al serializar
    /// la respuesta que se persiste para un replay.
    /// </summary>
    private static Sales.Module.Interfaces.CashAdvanceResultDto MapCashAdvanceLocalTimes(
        Sales.Module.Interfaces.CashAdvanceResultDto result,
        TimeZoneInfo tz)
    {
        result.ExpenseTransaction = MapLocalTime(result.ExpenseTransaction, tz);
        result.IncomeTransaction = MapLocalTime(result.IncomeTransaction, tz);
        return result;
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

    [RequireSecurityStampValidation]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpPost("cash-advance")]
    public async Task<ActionResult<CashAdvanceResultDto>> ProcessCashAdvanceAsync([FromBody] CashAdvanceRequest request, CancellationToken cancellationToken)
    {
        if (_currentUserService.UserId == null || !int.TryParse(_currentUserService.UserId, out int authUserId))
        {
            return this.ApiUnauthorized("Sesión inválida.");
        }

        const string requestPath = "/api/cashdrawer/cash-advance";
        var idempotency = await _idempotencyResolver.ResolveAsync(
            this,
            requestPath,
            System.Text.Json.JsonSerializer.Serialize(request),
            missingKeyMessage: "El encabezado Idempotency-Key es obligatorio para procesar adelantos de efectivo.");
        if (idempotency.ShouldStop)
        {
            return idempotency.BlockingResult!;
        }

        var user = await _userService.GetUserAsync(authUserId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Usuario no encontrado.");
        int cashierId = user.Id;
        string userName = user.Name;

        // 8.154 (SEC-04): anclar la tasa antes del coordinador para que comisión y movimientos usen la tasa anclada.
        decimal anchoredRate = await ResolveAnchoredRateAsync(request.ExchangeRate, referenceId: request.SessionId, cancellationToken: cancellationToken);

        try
        {
            var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);

            // 8.202-H2 (S4): la respuesta idempotente se persiste dentro de la MISMA transacción
            // que la venta y los dos movimientos de gaveta. El serializador congela la hora local.
            Sales.Module.DTOs.CashAdvanceIdempotency? advanceIdempotency =
                idempotency.Key is not null && idempotency.PayloadHash is not null
                    ? new Sales.Module.DTOs.CashAdvanceIdempotency(
                        idempotency.Key,
                        requestPath,
                        idempotency.PayloadHash,
                        _idempotencyResolver.GetActorUserId(),
                        advance => System.Text.Json.JsonSerializer.Serialize(MapCashAdvanceLocalTimes(advance, tz)))
                    : null;

            var result = await _cashAdvanceCoordinator.ProcessAsync(
                request.SessionId,
                request.RequestedAmountLocal,
                request.PaymentMethodId,
                request.PaymentMethodName,
                request.IsTransfer,
                anchoredRate,
                cashierId,
                userName,
                cancellationToken,
                advanceIdempotency);

            result = MapCashAdvanceLocalTimes(result, tz);

            return Ok(result);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IdempotencyRequestResolver.IsIdempotencyUniqueViolation(ex))
        {
            return await _idempotencyResolver.HandleCollisionAsync(this, ex, requestPath, idempotency.Key, idempotency.PayloadHash);
        }
    }
}
