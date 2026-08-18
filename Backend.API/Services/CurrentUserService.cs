using System;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Backend.API.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public UserRole? UserRole
    {
        get
        {
            // 1. Prioritize JWT Claims
            var roleClaim = _httpContextAccessor.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
            if (!string.IsNullOrEmpty(roleClaim) && Enum.TryParse<UserRole>(roleClaim, true, out var role))
            {
                return role;
            }

            // 2. Temporary fallback for unauthenticated / legacy requests
            var headerRole = _httpContextAccessor.HttpContext?.Request.Headers["X-User-Role"].ToString();
            if (!string.IsNullOrEmpty(headerRole) && Enum.TryParse<UserRole>(headerRole, true, out var legacyRole))
            {
                return legacyRole;
            }

            return null;
        }
    }

    public string? UserId
    {
        get
        {
            // 1. Prioritize JWT Claims
            var idClaim = _httpContextAccessor.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? _httpContextAccessor.HttpContext?.User?.FindFirst("sub")?.Value;
            if (!string.IsNullOrEmpty(idClaim))
            {
                return idClaim;
            }

            // 2. Temporary fallback for unauthenticated / legacy requests
            return _httpContextAccessor.HttpContext?.Request.Headers["X-User-Id"].ToString();
        }
    }

    public bool CanMutateCatalog => UserRole == null || UserRole != Core.Entities.UserRole.Cashier;
    public bool CanMutateSettings => UserRole == null || UserRole != Core.Entities.UserRole.Cashier;
    public bool CanMutateExchangeRate => UserRole == null || UserRole != Core.Entities.UserRole.Cashier;
}
