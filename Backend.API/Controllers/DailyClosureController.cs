using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Attributes;
using Backend.API.Services;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DailyClosureController : ControllerBase
{
    private readonly IDailyClosureService _closureService;
    private readonly ICurrentUserService _currentUserService;
    // 8.149 (SRE-02): clave obligatoria + replay para POST /api/dailyclosure.
    private readonly IdempotencyRequestResolver _idempotencyResolver;

    public DailyClosureController(
        IDailyClosureService closureService,
        ICurrentUserService currentUserService,
        IIdempotencyService? idempotencyService = null)
    {
        _closureService = closureService;
        _currentUserService = currentUserService;
        _idempotencyResolver = new IdempotencyRequestResolver(idempotencyService, currentUserService);
    }

    [HttpGet("expected-totals")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<List<ExpectedTotalDto>>> GetExpectedTotalsAsync([FromQuery] DateTime dateUtc, CancellationToken cancellationToken)
    {
        if (dateUtc == default)
        {
            return this.ApiBadRequest("El parámetro dateUtc es obligatorio.");
        }

        var totals = await _closureService.GetExpectedTotalsByPaymentMethodAsync(dateUtc, cancellationToken);
        return Ok(totals);
    }

    [RequireSecurityStampValidation]
    [HttpPost]
    public async Task<ActionResult> CreateClosureAsync([FromBody] CreateClosureRequest request, CancellationToken cancellationToken)
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

        // 8.149 (SRE-02): clave obligatoria + replay/422 antes de resolver fecha/tasa y persistir.
        string requestPath = "/api/dailyclosure";
        string bodyJson = _idempotencyResolver.GetActorUserId() + "|" + System.Text.Json.JsonSerializer.Serialize(request);
        var resolved = await _idempotencyResolver.ResolveAsync(this, requestPath, bodyJson);
        if (resolved.ShouldStop) return resolved.BlockingResult!;

        return await ExecuteCreateClosureAsync(request, cancellationToken, resolved, requestPath);
    }

    private async Task<ActionResult> ExecuteCreateClosureAsync(CreateClosureRequest request, CancellationToken cancellationToken, IdempotencyResolution? idempotency = null, string? requestPath = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
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

            var result = await _closureService.CreateClosureFromCommandAsync(command, cancellationToken);

            if (idempotency.HasValue && requestPath != null)
            {
                await _idempotencyResolver.RegisterSuccessAsync(idempotency.Value, requestPath, System.Text.Json.JsonSerializer.Serialize(result), cancellationToken);
            }

            if (Response?.Headers != null)
            {
                Response.Headers["X-Cache-Lookup"] = "MISS";
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message == Sales.Module.Services.DailyClosureService.DuplicateClosureDateMessage)
        {
            return this.ApiConflict(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (DbUpdateException ex) when (IsClosureDateUniqueViolation(ex))
        {
            // Carrera entre el guard serializable y el índice único: mismo conflicto explícito.
            return this.ApiConflict(Sales.Module.Services.DailyClosureService.DuplicateClosureDateMessage);
        }
        catch (DbUpdateException ex) when (idempotency.HasValue && IdempotencyRequestResolver.IsIdempotencyUniqueViolation(ex))
        {
            return await _idempotencyResolver.HandleCollisionAsync(this, ex, requestPath!, idempotency.Value.Key, idempotency.Value.PayloadHash);
        }
        catch (DbUpdateException)
        {
            return this.ApiConflict("Conflicto de concurrencia al registrar el cierre diario. Es posible que ya se haya ejecutado otro cierre en paralelo.");
        }
    }

    private static bool IsClosureDateUniqueViolation(DbUpdateException ex)
    {
        string combined = ex.Message + "|" + (ex.InnerException?.Message ?? "");
        return combined.Contains("IX_DailyClosures_ClosureDate", StringComparison.Ordinal)
            || combined.Contains("DailyClosures.ClosureDate", StringComparison.Ordinal);
    }

    private string ResolveUserId()
    {
        string? authenticatedUserId = _currentUserService.UserId
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.Identity?.Name;

        return !string.IsNullOrWhiteSpace(authenticatedUserId)
            ? authenticatedUserId
            : Core.Constants.SecurityConstants.RootAdminUsername;
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
    public async Task<ActionResult<DailyClosureResponseDto>> GetClosureAsync(int id, CancellationToken cancellationToken)
    {
        var closure = await _closureService.GetClosureAsync(id, cancellationToken);
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

