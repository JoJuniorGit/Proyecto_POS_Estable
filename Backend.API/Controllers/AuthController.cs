using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Core.DTOs;
using Core.Interfaces;
using Core.Logging;
using Backend.API.Services;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[EnableRateLimiting("AuthRateLimit")]
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly SalesDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IPasswordPolicyService _passwordPolicyService;
    private readonly ISecurityStampValidator? _stampValidator;

    private static readonly string _dummyPasswordHash = PasswordHasher.HashPassword("dummy-ooac0f8b");

    private async Task<Core.Entities.User?> FindUserByCedulaAsync(string searchInput, CancellationToken cancellationToken = default)
    {
        var searchLower = searchInput.ToLower();
        var withV = searchInput.StartsWith("V-", StringComparison.OrdinalIgnoreCase) ? searchLower : "v-" + searchLower;
        var digitsOnly = System.Text.RegularExpressions.Regex.Replace(searchInput, @"[^\d]", "");

        return await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == searchLower ||
                                                       u.Cedula.ToLower() == searchLower ||
                                                       u.Cedula.ToLower() == withV ||
                                                       (digitsOnly.Length > 0 && (u.Cedula.ToLower() == "v-" + digitsOnly || u.Cedula == digitsOnly)), cancellationToken);
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AuthController(
        SalesDbContext db, 
        ITokenService tokenService, 
        IPasswordPolicyService? passwordPolicyService = null,
        ISecurityStampValidator? stampValidator = null)
    {
        _db = db;
        _tokenService = tokenService;
        _passwordPolicyService = passwordPolicyService ?? new Core.Services.PasswordPolicyService();
        _stampValidator = stampValidator;
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

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResultDto>> LoginAsync([FromBody] LoginRequest request, System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Cedula))
        {
            return this.ApiBadRequest("El usuario es requerido.");
        }

        var searchInput = request.Cedula.Trim();
        var user = await FindUserByCedulaAsync(searchInput, cancellationToken);
        var obfCedula = ObfuscateCedula(request.Cedula);

        if (user == null)
        {
            PasswordHasher.VerifyPassword(request.Password, _dummyPasswordHash);
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión: Usuario '{obfCedula}' no encontrado.");
            return this.ApiUnauthorized("Credenciales inválidas.");
        }

        if (user.LockoutEndUtc.HasValue)
        {
            if (user.LockoutEndUtc.Value > DateTime.UtcNow)
            {
                AppLogger.LogWarn($"[AUTH] Intento de acceso a cuenta bloqueada: Usuario '{obfCedula}'. Bloqueada hasta {user.LockoutEndUtc.Value:O}.");
                return this.ApiUnauthorized("Credenciales inválidas.");
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
            return this.ApiUnauthorized("Credenciales inválidas.");
        }

        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión: Usuario '{obfCedula}' no tiene contraseña configurada.");
            return this.ApiUnauthorized("Credenciales inválidas.");
        }

        bool passwordMatches = !string.IsNullOrWhiteSpace(request.Password) && PasswordHasher.VerifyPassword(request.Password, user.PasswordHash);

        if (!passwordMatches)
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= 5 && (!user.LockoutEndUtc.HasValue || user.LockoutEndUtc.Value <= DateTime.UtcNow))
            {
                user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(15);
                AppLogger.LogWarn($"[AUTH] Usuario '{obfCedula}' alcanzó 5 intentos fallidos. Cuenta bloqueada por 15 minutos.");
            }
            await _db.SaveChangesAsync(cancellationToken);
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión para Usuario '{obfCedula}': Contraseña incorrecta (Intento {user.AccessFailedCount}/5).");
            return this.ApiUnauthorized("Credenciales inválidas.");
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
            return this.ApiPasswordChangeRequired("Debe cambiar su contraseña antes de continuar.");
        }

        var platform = request.Platform;
        if (string.IsNullOrWhiteSpace(platform) && Request?.Headers != null && Request.Headers.TryGetValue("X-Client-Platform", out var headerPlatform))
        {
            platform = headerPlatform.ToString();
        }

        bool isWeb = string.Equals(platform, "Web", StringComparison.OrdinalIgnoreCase);
        var scope = "pos:api";
        var token = _tokenService.GenerateToken(user, scope);

        if (isWeb && Response?.Cookies != null)
        {
            var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,
                Secure = Request?.IsHttps ?? false,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddMinutes(_tokenService.ExpiryMinutes)
            };
            Response.Cookies.Append("pos_jwt", token, cookieOptions);
        }

        var dto = new UserDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = string.IsNullOrWhiteSpace(user.Name) ? user.FullName : user.Name,
            Role = user.Role,
            IsActive = user.IsActive
        };

        return Ok(new LoginResultDto
        {
            User = dto,
            Token = isWeb ? null : token
        });
    }

    [NonAction]
    public Task<ActionResult<LoginResultDto>> Login(LoginRequest request) => LoginAsync(request);

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? User.FindFirst("sub")?.Value;
        if (int.TryParse(idClaim, out int uid))
        {
            if (_stampValidator != null)
            {
                await _stampValidator.RevokeUserStampAsync(uid);
            }
        }

        if (Response?.Cookies != null)
        {
            Response.Cookies.Delete("pos_jwt", new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,
                Secure = Request?.IsHttps ?? false,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
                Path = "/"
            });
        }
        return Ok(new { Message = "Sesión cerrada correctamente." });
    }

    [NonAction]
    public Task<IActionResult> Logout() => LogoutAsync();

    [AllowAnonymous]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordRequest request, System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Cedula) ||
            string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return this.ApiBadRequest("Usuario, contraseña actual y nueva contraseña son requeridos.");
        }

        var searchInput = request.Cedula.Trim();
        var user = await FindUserByCedulaAsync(searchInput, cancellationToken);
        if (user == null)
        {
            PasswordHasher.VerifyPassword(request.CurrentPassword, _dummyPasswordHash);
            return this.ApiUnauthorized("Credenciales inválidas o contraseña actual incorrecta.");
        }

        if (user.LockoutEndUtc.HasValue)
        {
            if (user.LockoutEndUtc.Value > DateTime.UtcNow)
            {
                AppLogger.LogWarn($"[AUTH] Intento de cambio de contraseña denegado: Usuario '{ObfuscateCedula(user.Username)}' bloqueado temporalmente.");
                return this.ApiUnauthorized("Credenciales inválidas o contraseña actual incorrecta.");
            }
            else
            {
                user.AccessFailedCount = 0;
                user.LockoutEndUtc = null;
            }
        }

        if (!user.IsActive)
        {
            return this.ApiUnauthorized("Credenciales inválidas o contraseña actual incorrecta.");
        }

        if (!PasswordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= 5 && (!user.LockoutEndUtc.HasValue || user.LockoutEndUtc.Value <= DateTime.UtcNow))
            {
                user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(15);
                AppLogger.LogSecurityAudit($"[ACCOUNT_LOCKED] Usuario={ObfuscateCedula(user.Username)} bloqueado por 15 min tras fallos en change-password.");
            }
            await _db.SaveChangesAsync(cancellationToken);
            AppLogger.LogStart($"[AUTH] Intento fallido en change-password para Usuario '{ObfuscateCedula(user.Username)}': Contraseña incorrecta (Intento {user.AccessFailedCount}/5).");
            return this.ApiUnauthorized("Credenciales inválidas o contraseña actual incorrecta.");
        }

        var (isPolicyValid, policyError) = _passwordPolicyService.ValidatePassword(request.NewPassword, user.Username);
        if (!isPolicyValid)
        {
            return this.ApiBadRequest(policyError);
        }

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
            user.PasswordHash = PasswordHasher.HashPassword(request.NewPassword);
            user.MustChangePassword = false;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await _db.SaveChangesAsync(cancellationToken);
            if (tx != null)
            {
                await tx.CommitAsync(cancellationToken);
            }
        });

        _stampValidator?.InvalidateUserStamp(user.Id);
        AppLogger.LogSecurityAudit($"[PASSWORD_CHANGED] UserId={user.Id}, Username={ObfuscateCedula(user.Username)}, Timestamp={DateTime.UtcNow:O}");

        if (Response?.Cookies != null)
        {
            Response.Cookies.Delete("pos_jwt", new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,
                Secure = Request?.IsHttps ?? false,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
                Path = "/"
            });
        }

        return Ok(new { Message = "Contraseña actualizada correctamente. Inicie sesión con su nueva clave." });
    }

    [NonAction]
    public Task<IActionResult> ChangePassword(ChangePasswordRequest request) => ChangePasswordAsync(request);

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> GetMeAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                   ?? User.FindFirst("sub")?.Value;
        if (!int.TryParse(idClaim, out int currentUserId))
        {
            return this.ApiUnauthorized("No autorizado.");
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
        if (user == null || !user.IsActive)
        {
            return this.ApiUnauthorized("No autorizado.");
        }

        return Ok(new UserDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = string.IsNullOrWhiteSpace(user.Name) ? user.FullName : user.Name,
            Role = user.Role,
            IsActive = user.IsActive
        });
    }

    [NonAction]
    public Task<ActionResult<UserDto>> GetMe() => GetMeAsync();
}
