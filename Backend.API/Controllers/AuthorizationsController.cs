using System.Security.Claims;
using Backend.API.Hubs;
using Backend.API.Services;
using Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.DTOs;
using Sales.Module.Services;

namespace Backend.API.Controllers;

/// <summary>
/// 8.150 (T4, design D4): par REST del hub para clientes con el socket caido. Create y estado
/// son [Authorize]; la resolucion remota exige rol Admin/Manager; el fallback local valida las
/// credenciales del supervisor dentro del servicio. Los mensajes exactos viven en
/// <see cref="AuthorizationMessages"/>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AuthorizationsController : ControllerBase
{
    private readonly IAuthorizationCoordinator _coordinator;

    public AuthorizationsController(IAuthorizationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync(
        [FromBody] RequestAuthorizationContract request,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadIdentity(out var identity))
        {
            return this.ApiUnauthorized("No autorizado.");
        }

        var result = await _coordinator.CreateAsync(request, identity.UserId, identity.Name, identity.Role, cancellationToken);

        if (result.Request is not null
            && result.Outcome is CreateAuthorizationOutcome.Created or CreateAuthorizationOutcome.Deduplicated)
        {
            return Ok(new
            {
                requestId = result.Request.Id,
                actionType = result.Request.ActionType.ToString(),
                saleId = result.Request.SaleId,
                requestedByName = result.Request.RequestedByName,
                terminal = result.Request.Terminal,
                status = result.Request.Status.ToString(),
                remainingLifetimeSeconds = result.Request.RemainingLifetimeSeconds,
                expiresAt = result.Request.ExpiresAt,
                deduplicated = result.Outcome == CreateAuthorizationOutcome.Deduplicated
            });
        }

        return result.Outcome switch
        {
            CreateAuthorizationOutcome.ElevationNotRequired => this.ApiConflict(result.Message),
            CreateAuthorizationOutcome.DriverBlocked => this.ApiForbidden(result.Message),
            CreateAuthorizationOutcome.SaleAccessDenied => this.ApiForbidden(result.Message),
            _ => this.ApiBadRequest(result.Message)
        };
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetStatusAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!TryReadIdentity(out var identity))
        {
            return this.ApiUnauthorized("No autorizado.");
        }

        var status = await _coordinator.GetStatusAsync(
            id,
            identity.UserId,
            AuthorizationService.IsElevatedRole(identity.Role),
            cancellationToken);

        if (status is null)
        {
            return this.ApiNotFound(AuthorizationMessages.RequestNotFound);
        }

        return Ok(new
        {
            id = status.Request.Id,
            actionType = status.Request.ActionType.ToString(),
            saleId = status.Request.SaleId,
            requestedByUserId = status.Request.RequestedByUserId,
            requestedByName = status.Request.RequestedByName,
            terminal = status.Request.Terminal,
            status = status.Request.Status.ToString(),
            resolutionMode = status.Request.ResolutionMode?.ToString(),
            resolvedByUserId = status.Request.ResolvedByUserId,
            resolvedByName = status.Request.ResolvedByName,
            reason = status.Request.ResolutionReason,
            contextJson = status.Request.ContextJson,
            createdAt = status.Request.CreatedAt,
            expiresAt = status.Request.ExpiresAt,
            resolvedAt = status.Request.ResolvedAt,
            consumedAt = status.Request.ConsumedAt,
            remainingLifetimeSeconds = status.Request.RemainingLifetimeSeconds,
            token = status.Token
        });
    }

    [HttpPost("{id:int}/resolve")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ResolveAsync(
        int id,
        [FromBody] ResolveAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadIdentity(out var identity))
        {
            return this.ApiUnauthorized("No autorizado.");
        }

        var result = await _coordinator.ResolveAsync(
            id,
            identity.UserId,
            identity.Name,
            request.Approved,
            request.Reason,
            cancellationToken);

        return result.Outcome switch
        {
            ResolveAuthorizationOutcome.Resolved => Ok(new
            {
                requestId = result.Request!.Id,
                status = result.Request.Status.ToString(),
                approved = request.Approved,
                resolvedByName = result.Request.ResolvedByName,
                resolutionMode = result.Request.ResolutionMode?.ToString(),
                reason = result.Request.ResolutionReason
            }),
            ResolveAuthorizationOutcome.AlreadyResolved => this.ApiConflict(result.Message),
            ResolveAuthorizationOutcome.Expired => this.ApiConflict(result.Message),
            _ => this.ApiNotFound(result.Message)
        };
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> CancelAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!TryReadIdentity(out var identity))
        {
            return this.ApiUnauthorized("No autorizado.");
        }

        var result = await _coordinator.CancelAsync(id, identity.UserId, cancellationToken);

        return result.Outcome switch
        {
            CancelAuthorizationOutcome.Cancelled => Ok(new
            {
                requestId = result.Request!.Id,
                status = result.Request.Status.ToString(),
                resolvedAt = result.Request.ResolvedAt
            }),
            CancelAuthorizationOutcome.AlreadyResolved => this.ApiConflict(result.Message),
            CancelAuthorizationOutcome.Expired => this.ApiConflict(result.Message),
            CancelAuthorizationOutcome.Forbidden => this.ApiForbidden(result.Message),
            _ => this.ApiNotFound(result.Message)
        };
    }

    [HttpPost("{id:int}/local-resolve")]
    public async Task<IActionResult> ResolveLocalAsync(
        int id,
        [FromBody] LocalResolveAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadIdentity(out _))
        {
            return this.ApiUnauthorized("No autorizado.");
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return this.ApiBadRequest(AuthorizationMessages.InvalidCredentials);
        }

        var result = await _coordinator.ResolveLocalAsync(id, request.Username, request.Password, request.Reason, cancellationToken);

        return result.Outcome switch
        {
            LocalResolveOutcome.Resolved => Ok(new
            {
                token = result.Token,
                supervisorUserId = result.SupervisorUserId,
                supervisorName = result.SupervisorName,
                status = result.Request?.Status.ToString()
            }),
            LocalResolveOutcome.InvalidCredentials or LocalResolveOutcome.LockedOut => this.ApiBadRequest(result.Message),
            LocalResolveOutcome.AlreadyResolved => this.ApiConflict(result.Message),
            LocalResolveOutcome.Expired => this.ApiConflict(result.Message),
            _ => this.ApiNotFound(result.Message)
        };
    }

    private bool TryReadIdentity(out ControllerIdentity identity)
    {
        identity = default;

        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (!int.TryParse(idClaim, out var userId) || userId <= 0)
        {
            return false;
        }

        var roleClaim = User.FindFirst(ClaimTypes.Role)?.Value;
        if (!Enum.TryParse<UserRole>(roleClaim, ignoreCase: true, out var role))
        {
            return false;
        }

        var name = User.FindFirst(ClaimTypes.Name)?.Value;
        identity = new ControllerIdentity(userId, string.IsNullOrWhiteSpace(name) ? $"Usuario {userId}" : name, role);
        return true;
    }

    private readonly record struct ControllerIdentity(int UserId, string Name, UserRole Role);
}

public sealed record ResolveAuthorizationRequest(bool Approved, string? Reason);

public sealed record LocalResolveAuthorizationRequest(string Username, string Password, string? Reason);
