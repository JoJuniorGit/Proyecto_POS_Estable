using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Core.Logging;
using Core.Security;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class AuthService : IAuthService
{
    private const int MaxFailedAttempts = 5;
    private const int LockoutMinutes = 15;

    private static readonly string _dummyPasswordHash = PasswordHasher.HashPassword("dummy-ooac0f8b");

    private readonly SalesDbContext _db;
    private readonly IPasswordPolicyService _passwordPolicyService;

    public AuthService(SalesDbContext db, IPasswordPolicyService? passwordPolicyService = null)
    {
        _db = db;
        _passwordPolicyService = passwordPolicyService ?? new Core.Services.PasswordPolicyService();
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
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión: Usuario '{obfCedula}' no encontrado.");
            return new AuthenticationResult(AuthenticationOutcome.InvalidCredentials);
        }

        if (user.LockoutEndUtc.HasValue)
        {
            if (user.LockoutEndUtc.Value > DateTime.UtcNow)
            {
                AppLogger.LogWarn($"[AUTH] Intento de acceso a cuenta bloqueada: Usuario '{obfCedula}'. Bloqueada hasta {user.LockoutEndUtc.Value:O}.");
                return new AuthenticationResult(AuthenticationOutcome.InvalidCredentials);
            }
            else
            {
                user.AccessFailedCount = 0;
                user.LockoutEndUtc = null;
            }
        }

        if (!user.IsActive)
        {
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión para Usuario '{obfCedula}': Usuario inactivo.");
            return new AuthenticationResult(AuthenticationOutcome.InvalidCredentials);
        }

        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión: Usuario '{obfCedula}' no tiene contraseña configurada.");
            return new AuthenticationResult(AuthenticationOutcome.InvalidCredentials);
        }

        bool passwordMatches = !string.IsNullOrWhiteSpace(password) && PasswordHasher.VerifyPassword(password, user.PasswordHash);

        if (!passwordMatches)
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= MaxFailedAttempts && (!user.LockoutEndUtc.HasValue || user.LockoutEndUtc.Value <= DateTime.UtcNow))
            {
                user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                AppLogger.LogWarn($"[AUTH] Usuario '{obfCedula}' alcanzó 5 intentos fallidos. Cuenta bloqueada por 15 minutos.");
            }
            await _db.SaveChangesAsync(cancellationToken);
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión para Usuario '{obfCedula}': Contraseña incorrecta (Intento {user.AccessFailedCount}/5).");
            return new AuthenticationResult(AuthenticationOutcome.InvalidCredentials);
        }

        if (user.AccessFailedCount > 0 || user.LockoutEndUtc.HasValue)
        {
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
        }
        user.LastLoginUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(user.SecurityStamp))
        {
            user.SecurityStamp = Guid.NewGuid().ToString("N");
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

        if (user.LockoutEndUtc.HasValue)
        {
            if (user.LockoutEndUtc.Value > DateTime.UtcNow)
            {
                AppLogger.LogWarn($"[AUTH] Intento de cambio de contraseña denegado: Usuario '{ObfuscateCedula(user.Username)}' bloqueado temporalmente.");
                return new PasswordChangeResult(PasswordChangeOutcome.InvalidCredentials);
            }
            else
            {
                user.AccessFailedCount = 0;
                user.LockoutEndUtc = null;
            }
        }

        if (!user.IsActive)
        {
            return new PasswordChangeResult(PasswordChangeOutcome.InvalidCredentials);
        }

        if (!PasswordHasher.VerifyPassword(currentPassword, user.PasswordHash))
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= MaxFailedAttempts && (!user.LockoutEndUtc.HasValue || user.LockoutEndUtc.Value <= DateTime.UtcNow))
            {
                user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                AppLogger.LogSecurityAudit($"[ACCOUNT_LOCKED] Usuario={ObfuscateCedula(user.Username)} bloqueado por 15 min tras fallos en change-password.");
            }
            await _db.SaveChangesAsync(cancellationToken);
            AppLogger.LogStart($"[AUTH] Intento fallido en change-password para Usuario '{ObfuscateCedula(user.Username)}': Contraseña incorrecta (Intento {user.AccessFailedCount}/5).");
            return new PasswordChangeResult(PasswordChangeOutcome.InvalidCredentials);
        }

        var (isPolicyValid, policyError) = _passwordPolicyService.ValidatePassword(newPassword, user.Username);
        if (!isPolicyValid)
        {
            return new PasswordChangeResult(PasswordChangeOutcome.PolicyViolation, PolicyError: policyError);
        }

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
            user.PasswordHash = PasswordHasher.HashPassword(newPassword);
            user.MustChangePassword = false;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await _db.SaveChangesAsync(cancellationToken);
            if (tx != null)
            {
                await tx.CommitAsync(cancellationToken);
            }
        });

        AppLogger.LogSecurityAudit($"[PASSWORD_CHANGED] UserId={user.Id}, Username={ObfuscateCedula(user.Username)}, Timestamp={DateTime.UtcNow:O}");
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
