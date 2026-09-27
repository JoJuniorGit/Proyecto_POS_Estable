using Core.DTOs;
using Core.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>
/// Application service that owns the authentication flows (login, change-password and
/// current-user lookup). Token generation and the HTTP response shaping stay in the API layer.
/// </summary>
public interface IAuthService
{
    Task<AuthenticationResult> AuthenticateAsync(string cedula, string password, CancellationToken cancellationToken = default);

    Task<PasswordChangeResult> ChangePasswordAsync(string cedula, string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    Task<UserDto?> GetCurrentUserAsync(int userId, CancellationToken cancellationToken = default);
}

public enum AuthenticationOutcome
{
    InvalidCredentials,
    PasswordChangeRequired,
    Success
}

public enum PasswordChangeOutcome
{
    InvalidCredentials,
    PolicyViolation,
    Success
}

/// <summary>
/// Result of an authentication attempt. On success it carries the tracked <see cref="User"/>
/// entity because ITokenService.GenerateToken requires it and, by dependency direction,
/// AuthService cannot depend on the Backend.API token service.
/// </summary>
public sealed record AuthenticationResult(AuthenticationOutcome Outcome, User? User = null);

public sealed record PasswordChangeResult(PasswordChangeOutcome Outcome, int UserId = 0, string? PolicyError = null);
