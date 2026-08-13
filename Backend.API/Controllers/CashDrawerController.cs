using Microsoft.AspNetCore.Mvc;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System.Threading.Tasks;

using Core.Interfaces;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CashDrawerController : ControllerBase
{
    private readonly ICashDrawerService _cashDrawerService;
    private readonly ISystemSettingsService _settingsService;
    private readonly Sales.Module.Data.SalesDbContext _db;
    private readonly ICurrentUserService _currentUserService;

    public CashDrawerController(
        ICashDrawerService cashDrawerService, 
        ISystemSettingsService settingsService, 
        Sales.Module.Data.SalesDbContext db,
        ICurrentUserService currentUserService)
    {
        _cashDrawerService = cashDrawerService;
        _settingsService = settingsService;
        _db = db;
        _currentUserService = currentUserService;
    }

    [HttpGet("active-session")]
    public async Task<ActionResult<CashDrawerSession>> GetActiveSession()
    {
        var rate = decimal.Parse(await _settingsService.GetSettingAsync("CurrentExchangeRate") ?? "1.0");
        var session = await _cashDrawerService.GetOrCreateActiveSessionAsync(rate);
        await MapLocalTimesAsync(session);

        if (session != null)
        {
            var responseSession = new CashDrawerSession
            {
                Id = session.Id,
                OpenedAt = session.OpenedAt,
                OpenedAtLocal = session.OpenedAtLocal,
                ClosedAt = session.ClosedAt,
                ClosedAtLocal = session.ClosedAtLocal,
                OpeningBalanceLocal = session.OpeningBalanceLocal,
                ClosingBalanceLocal = session.ClosingBalanceLocal,
                OpeningExchangeRate = session.OpeningExchangeRate,
                ClosingExchangeRate = session.ClosingExchangeRate,
                Status = session.Status,
                Transactions = session.Transactions
                    .Where(t => t.IsPhysicalCash)
                    .OrderByDescending(t => t.TransactionTime)
                    .ToList()
            };
            return Ok(responseSession);
        }

        return Ok(session);
    }

    [HttpPost("open")]
    public async Task<ActionResult<CashDrawerSession>> OpenSession([FromBody] OpenSessionRequest request)
    {
        try
        {
            var session = await _cashDrawerService.OpenSessionAsync(request.OpeningBalanceLocal, request.CurrentExchangeRate);
            await MapLocalTimesAsync(session);
            return Ok(session);
        }
        catch (System.Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpPost("close")]
    public async Task<ActionResult<CashDrawerSession>> CloseSession([FromBody] CloseSessionRequest request)
    {
        try
        {
            var session = await _cashDrawerService.CloseSessionAsync(request.ActualClosingBalanceLocal, request.CurrentExchangeRate);
            await MapLocalTimesAsync(session);
            return Ok(session);
        }
        catch (System.Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpGet("current-balance")]
    public async Task<ActionResult<decimal>> GetCurrentBalance([FromQuery] int sessionId)
    {
        var balance = await _cashDrawerService.GetCurrentBalanceLocalAsync(sessionId);
        return Ok(balance);
    }

    [HttpPost("transaction")]
    public async Task<ActionResult<CashTransaction>> AddTransaction([FromBody] AddTransactionRequest request)
    {
        try
        {
            // Permission check: only Admins can execute manual CashIn or CashOut transactions
            if ((request.Source == CashTransactionSource.CashIn || request.Source == CashTransactionSource.CashOut || request.Source == CashTransactionSource.ManualAdjustment) &&
                _currentUserService.UserRole.HasValue && _currentUserService.UserRole.Value == Core.Entities.UserRole.Cashier)
            {
                return BadRequest(new { Message = "Acceso denegado: Únicamente los usuarios administradores pueden realizar operaciones manuales de ingreso (CASH IN) o retiro (CASH OUT) en la caja." });
            }

            // Convert purely integer local cash strictly to USD equivalent for standard tracking
            decimal amountUsd = request.AmountLocal / request.ExchangeRate;
            
            var transaction = await _cashDrawerService.AddTransactionAsync(
                        request.SessionId,
                        request.Type,
                        request.Source,
                        request.AmountLocal,
                        amountUsd,
                        request.ExchangeRate,
                        request.Description,
                        null
            );
            // Transaction Mapping logic
            var tzId = await _settingsService.GetSettingAsync("SelectedTimeZoneId");
            var tz = Sales.Module.Helpers.TimeZoneHelper.GetTimeZone(tzId);
            transaction.TransactionTimeLocal = System.TimeZoneInfo.ConvertTimeFromUtc(transaction.TransactionTime, tz);
            return Ok(transaction);
        }
        catch (System.Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    private async Task MapLocalTimesAsync(CashDrawerSession session)
    {
        var tzId = await _settingsService.GetSettingAsync("SelectedTimeZoneId");
        var tz = Sales.Module.Helpers.TimeZoneHelper.GetTimeZone(tzId);

        session.OpenedAtLocal = System.TimeZoneInfo.ConvertTimeFromUtc(session.OpenedAt, tz);
        if (session.ClosedAt.HasValue)
        {
            session.ClosedAtLocal = System.TimeZoneInfo.ConvertTimeFromUtc(session.ClosedAt.Value, tz);
        }

        foreach (var tx in session.Transactions)
        {
            tx.TransactionTimeLocal = System.TimeZoneInfo.ConvertTimeFromUtc(tx.TransactionTime, tz);
            if (tx.Sale != null && tx.Sale.InvoiceNumber.HasValue)
            {
                tx.Description = $"Factura N° {tx.Sale.InvoiceNumber.Value}";
            }
        }
    }

    [HttpPost("cash-advance")]
    public async Task<ActionResult<CashAdvanceResultDto>> ProcessCashAdvance([FromBody] CashAdvanceRequest request)
    {
        try
        {
            int? cashierId = request.CashierId;
            if (!cashierId.HasValue && Request.Headers.TryGetValue("X-User-Id", out var headerUserId) && int.TryParse(headerUserId, out int parsedId))
            {
                cashierId = parsedId;
            }

            string userName = !string.IsNullOrWhiteSpace(request.UserName)
                ? request.UserName
                : (cashierId.HasValue ? (await _db.Users.FindAsync(cashierId.Value))?.Name ?? "Usuario" : "Usuario");

            var result = await _cashDrawerService.ProcessCashAdvanceAsync(
                request.SessionId,
                request.RequestedAmountLocal,
                request.PaymentMethodId,
                request.PaymentMethodName,
                request.IsTransfer,
                request.ExchangeRate,
                cashierId,
                userName);

            var tzId = await _settingsService.GetSettingAsync("SelectedTimeZoneId");
            var tz = Sales.Module.Helpers.TimeZoneHelper.GetTimeZone(tzId);
            result.ExpenseTransaction.TransactionTimeLocal = System.TimeZoneInfo.ConvertTimeFromUtc(result.ExpenseTransaction.TransactionTime, tz);
            result.IncomeTransaction.TransactionTimeLocal = System.TimeZoneInfo.ConvertTimeFromUtc(result.IncomeTransaction.TransactionTime, tz);

            return Ok(result);
        }
        catch (System.Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
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
