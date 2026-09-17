using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Attributes;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DailyClosureController : ControllerBase
{
    private readonly IDailyClosureService _closureService;
    private readonly ICurrentUserService _currentUserService;

    public DailyClosureController(
        IDailyClosureService closureService,
        ICurrentUserService currentUserService)
    {
        _closureService = closureService;
        _currentUserService = currentUserService;
    }

    [HttpGet("expected-totals")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<List<ExpectedTotalDto>>> GetExpectedTotals([FromQuery] DateTime dateUtc)
    {
        if (dateUtc == default)
        {
            return Problem(
                detail: "El parámetro dateUtc es obligatorio.",
                statusCode: 400,
                title: "Parámetro inválido",
                type: "https://tools.ietf.org/html/rfc7231#section-6.5.1");
        }

        var totals = await _closureService.GetExpectedTotalsByPaymentMethodAsync(dateUtc);
        return Ok(totals);
    }

    [RequireSecurityStampValidation]
    [HttpPost]
    public async Task<ActionResult> CreateClosure([FromBody] CreateClosureRequest request, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Driver"))
        {
            return this.ApiForbidden("El rol Driver no tiene permisos para registrar cierres diarios.");
        }

        string? validationError = ValidateClosureRequest(request);
        if (validationError is not null)
        {
            return this.ApiBadRequest(validationError);
        }

        return await ExecuteCreateClosureAsync(request, cancellationToken);
    }

    private async Task<ActionResult> ExecuteCreateClosureAsync(CreateClosureRequest request, CancellationToken cancellationToken)
    {
        string finalUserId = ResolveUserId();
        DateTime closureDate = ResolveClosureDate(request.ClosureDate, finalUserId, User.IsInRole("Admin"));

        var command = new CreateClosureCommand(
            ClosureDateUtc: closureDate,
            UserId: finalUserId,
            Observation: request.Observation,
            Declarations: request.Details
                .Select(d => new DeclaredPaymentAmount(d.PaymentMethodId, d.ActualAmountBsS))
                .ToList());

        try
        {
            var result = await _closureService.CreateClosureFromCommandAsync(command, cancellationToken);

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (DbUpdateException)
        {
            return this.ApiConflict("Conflicto de concurrencia al registrar el cierre diario. Es posible que ya se haya ejecutado otro cierre en paralelo.");
        }
    }

    private string ResolveUserId()
    {
        string? authenticatedUserId = _currentUserService.UserId
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.Identity?.Name;

        return !string.IsNullOrWhiteSpace(authenticatedUserId)
            ? authenticatedUserId
            : "Admin";
    }

    private static string? ValidateClosureRequest(CreateClosureRequest? request)
    {
        if (request?.Details is null || request.Details.Count == 0)
        {
            return "El arqueo debe incluir el desglose por métodos de pago.";
        }

        var duplicatedMethodIds = request.Details
            .GroupBy(d => d.PaymentMethodId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        return duplicatedMethodIds.Count > 0
            ? $"El desglose contiene métodos de pago duplicados: {string.Join(", ", duplicatedMethodIds)}."
            : null;
    }

    [HttpGet("{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult> GetClosure(int id)
    {
        var closure = await _closureService.GetClosureAsync(id);
        if (closure == null) return NotFound();
        return Ok(closure);
    }

    private static DateTime ResolveClosureDate(DateTime requestDate, string userId, bool isAdmin)
    {
        if (requestDate == default)
        {
            return DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        var requestedDateUtc = requestDate.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(requestDate, DateTimeKind.Utc)
            : requestDate.ToUniversalTime();

        if (!isAdmin)
        {
            return now;
        }

        if (requestedDateUtc > now.AddMinutes(5))
        {
            throw new InvalidOperationException("La fecha de cierre no puede ser en el futuro.");
        }

        if (now - requestedDateUtc > TimeSpan.FromHours(24))
        {
            throw new InvalidOperationException("No se permite registrar cierres con más de 24 horas de retroactividad.");
        }

        Core.Logging.AppLogger.LogSecurityAudit(
            $"[DAILY_CLOSURE_BACKDATE] Admin '{userId}' registró un cierre con fecha retroactiva: {requestedDateUtc:O} (Actual: {now:O})");

        return requestedDateUtc;
    }
}

public class CreateClosureRequest
{
    public DateTime ClosureDate { get; set; }
    public string? UserId { get; set; } = "Admin";
    public string? Observation { get; set; }
    public List<CreateClosureDetailRequest> Details { get; set; } = new();
}

public class CreateClosureDetailRequest
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal ExpectedAmountBsS { get; set; }
    public decimal ActualAmountBsS { get; set; }
}
