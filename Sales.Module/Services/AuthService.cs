using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Core.Logging;
using Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class AuthService : IAuthService
{
    private static readonly string _dummyPasswordHash = PasswordHasher.HashPassword("dummy-ooac0f8b");

    private readonly SalesDbContext _db;
    private readonly IPasswordPolicyService _passwordPolicyService;
    private readonly ILogger<AuthService>? _logger;

    public AuthService(
        SalesDbContext db,
        IPasswordPolicyService? passwordPolicyService = null,
        ILogger<AuthService>? logger = null)
    {
        _db = db;
        _passwordPolicyService = passwordPolicyService ?? new Core.Services.PasswordPolicyService();
        _logger = logger;
    }

    // AUD-12: this lookup MUST keep change tracking. The returned entity is mutated by the login
    // and change-password flows (AccessFailedCount, LockoutEndUtc, LastLoginUtc, SecurityStamp)
    // and persisted via SaveChangesAsync; AsNoTracking here would silently disable the
    // failed-attempt counter and the brute-force lockout.
    private async Task<User?> FindUserByCedulaAsync(string searchInput, CancellationToken cancellationToken = default)
    {
        var searchLower = searchInput.ToLower();
        var withV = searchInput.StartsWith("V-", StringComparison.OrdinalIgnoreCase) ? searchLower : "v-" + searchLower;
        var digitsOnly = Regex.Replace(searchInput, @"[^\d]", "");

        return await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == searchLower ||
                                                       u.Cedula.ToLower() == searchLower ||
                                                       u.Cedula.ToLower() == withV ||
                                                       (digitsOnly.Length > 0 && (u.Cedula.ToLower() == "v-" + digitsOnly || u.Cedula == digitsOnly)), cancellationToken);
    }

    public async Task<AuthenticationResult> AuthenticateAsync(string cedula, string password, CancellationToken cancellationToken = default)
    {
        var user = await FindUserByCedulaAsync(cedula, cancellationToken);
        var obfCedula = ObfuscateCedula(cedula);

        if (user == null)
        {
            PasswordHasher.VerifyPassword(password, _dummyPasswordHash);
            _logger?.LogWarning("Intento fallido de inicio de sesión: usuario {Username} no encontrado.", obfCedula);
            return new AuthenticationResult(AuthenticationOutcome.InvalidCredentials);
        }

        if (LoginLockoutPolicy.IsLockedOut(user, DateTime.UtcNow))
        {
            _logger?.LogWarning(
                "Intento de acceso a cuenta bloqueada: usuario {Username}, bloqueada hasta {LockoutEndUtc}.",
                obfCedula,
                user.LockoutEndUtc);
            return new AuthenticationResult(AuthenticationOutcome.InvalidCredentials);
        }

        LoginLockoutPolicy.ClearExpiredLockout(user, DateTime.UtcNow);

        if (!user.IsActive)
        {
            _logger?.LogWarning("Intento fallido de inicio de sesión: usuario {Username} inactivo.", obfCedula);
            return new AuthenticationResult(AuthenticationOutcome.InvalidCredentials);
        }

        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            _logger?.LogWarning("Intento fallido de inicio de sesión: usuario {Username} sin contraseña configurada.", obfCedula);
            return new AuthenticationResult(AuthenticationOutcome.InvalidCredentials);
        }

        bool passwordMatches = !string.IsNullOrWhiteSpace(password) && PasswordHasher.VerifyPassword(password, user.PasswordHash);

        if (!passwordMatches)
        {
            if (LoginLockoutPolicy.RegisterFailedAttempt(user, DateTime.UtcNow))
            {
                _logger?.LogWarning(
                    "Usuario {Username} alcanzó {MaxAttempts} intentos fallidos; cuenta bloqueada por {LockoutMinutes} minutos.",
                    obfCedula,
                    LoginLockoutPolicy.MaxFailedAttempts,
                    LoginLockoutPolicy.LockoutMinutes);
            }
            await _db.SaveChangesAsync(cancellationToken);
            _logger?.LogWarning(
                "Contraseña incorrecta para usuario {Username} (intento {Attempt}/{MaxAttempts}).",
                obfCedula,
                user.AccessFailedCount,
                LoginLockoutPolicy.MaxFailedAttempts);
            return new AuthenticationResult(AuthenticationOutcome.InvalidCredentials);
        }

        LoginLockoutPolicy.ResetOnSuccess(user);
        user.LastLoginUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(user.SecurityStamp))
        {
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        }

        // 8.204: rehash transparente de credenciales emitidas con la política anterior.
        if (PasswordHasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = PasswordHasher.HashPassword(password);
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (user.MustChangePassword)
        {
            return new AuthenticationResult(AuthenticationOutcome.PasswordChangeRequired);
        }

        return new AuthenticationResult(AuthenticationOutcome.Success, user);
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(string cedula, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await FindUserByCedulaAsync(cedula, cancellationToken);
        if (user == null)
        {
            PasswordHasher.VerifyPassword(currentPassword, _dummyPasswordHash);
            return new PasswordChangeResult(PasswordChangeOutcome.InvalidCredentials);
        }

        if (LoginLockoutPolicy.IsLockedOut(user, DateTime.UtcNow))
        {
            _logger?.LogWarning(
                "Cambio de contraseña denegado: usuario {Username} bloqueado temporalmente.",
                ObfuscateCedula(user.Username));
            return new PasswordChangeResult(PasswordChangeOutcome.InvalidCredentials);
        }

        LoginLockoutPolicy.ClearExpiredLockout(user, DateTime.UtcNow);

        if (!user.IsActive)
        {
            return new PasswordChangeResult(PasswordChangeOutcome.InvalidCredentials);
        }

        if (!PasswordHasher.VerifyPassword(currentPassword, user.PasswordHash))
        {
            if (LoginLockoutPolicy.RegisterFailedAttempt(user, DateTime.UtcNow))
            {
                AppLogger.LogStructuredSecurity(
                    "ACCOUNT_LOCKED",
                    user.Id,
                    "auth/change-password",
                    (string?)null,
                    "Lockout",
                    new { Username = ObfuscateCedula(user.Username), LockoutMinutes = LoginLockoutPolicy.LockoutMinutes });
            }
            await _db.SaveChangesAsync(cancellationToken);
            _logger?.LogWarning(
                "Contraseña actual incorrecta en change-password para usuario {Username} (intento {Attempt}/{MaxAttempts}).",
                ObfuscateCedula(user.Username),
                user.AccessFailedCount,
                LoginLockoutPolicy.MaxFailedAttempts);
            return new PasswordChangeResult(PasswordChangeOutcome.InvalidCredentials);
        }

        var (isPolicyValid, policyError) = _passwordPolicyService.ValidatePassword(newPassword, user.Username);
        if (!isPolicyValid)
        {
            return new PasswordChangeResult(PasswordChangeOutcome.PolicyViolation, PolicyError: policyError);
        }

        var newPasswordHash = PasswordHasher.HashPassword(newPassword);

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
            user.PasswordHash = newPasswordHash;
            user.MustChangePassword = false;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await _db.SaveChangesAsync(cancellationToken);
            if (tx != null)
            {
                await tx.CommitAsync(cancellationToken);
            }
        });

        AppLogger.LogStructuredSecurity(
            "PASSWORD_CHANGED",
            user.Id,
            "auth/change-password",
            (string?)null,
            "PasswordChanged",
            new { Username = ObfuscateCedula(user.Username) });
        return new PasswordChangeResult(PasswordChangeOutcome.Success, user.Id);
    }

    public async Task<UserDto?> GetCurrentUserAsync(int userId, CancellationToken cancellationToken = default)
    {
        // Read-only path: nothing mutates the entity afterwards, so AsNoTracking is safe here.
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user == null || !user.IsActive)
        {
            return null;
        }

        return ToUserDto(user);
    }

    private static UserDto ToUserDto(User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = string.IsNullOrWhiteSpace(user.Name) ? user.FullName : user.Name,
            Role = user.Role,
            IsActive = user.IsActive
        };
    }

    private static string ObfuscateCedula(string? cedula)
    {
        if (string.IsNullOrWhiteSpace(cedula)) return "***";
        var c = cedula.Trim();
        if (c.Length <= 4) return new string('*', c.Length);
        var isPrefixed = c.Length > 2 && c[1] == '-';
        var prefix = isPrefixed ? c.Substring(0, 2) : "";
        var numberPart = isPrefixed ? c.Substring(2) : c;
        if (numberPart.Length <= 4) return prefix + new string('*', numberPart.Length);
        return prefix + "***" + numberPart.Substring(numberPart.Length - 4);
    }
}
