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

            return null;
        }
    }

    public string? UserId
    {
        get
        {
            // Resolve identity strictly from JWT Claims
            var idClaim = _httpContextAccessor.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? _httpContextAccessor.HttpContext?.User?.FindFirst("sub")?.Value;
            if (!string.IsNullOrEmpty(idClaim))
            {
                return idClaim;
            }

            return null;
        }
    }

    // Deny-by-default: only explicitly allowed roles have mutation rights
    public bool CanMutateCatalog => UserRole == Core.Entities.UserRole.Admin;
    public bool CanMutateSettings => UserRole == Core.Entities.UserRole.Admin;
    public bool CanMutateExchangeRate => UserRole == Core.Entities.UserRole.Admin;
}
