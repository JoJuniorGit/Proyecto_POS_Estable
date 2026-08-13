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
            var headerRole = _httpContextAccessor.HttpContext?.Request.Headers["X-User-Role"].ToString();
            if (!string.IsNullOrEmpty(headerRole) && Enum.TryParse<UserRole>(headerRole, true, out var role))
            {
                return role;
            }
            return null;
        }
    }

    public string? UserId => _httpContextAccessor.HttpContext?.Request.Headers["X-User-Id"].ToString();

    public bool CanMutateCatalog => UserRole == null || UserRole != Core.Entities.UserRole.Cashier;
    public bool CanMutateSettings => UserRole == null || UserRole != Core.Entities.UserRole.Cashier;
    public bool CanMutateExchangeRate => UserRole == null || UserRole != Core.Entities.UserRole.Cashier;
}
