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

    // 8.9-M3: hash PBKDF2 fijo para ejecutar el mismo coste de derivación cuando el usuario
    // no existe (cierra el oráculo de timing por enumeración de cuenta).
    private static readonly string _dummyPasswordHash = PasswordHasher.HashPassword("dummy-ooac0f8b");

    private async Task<Core.Entities.User?> FindUserByCedulaAsync(string searchInput)
    {
        var searchLower = searchInput.ToLower();
        var withV = searchInput.StartsWith("V-", StringComparison.OrdinalIgnoreCase) ? searchLower : "v-" + searchLower;
        var digitsOnly = System.Text.RegularExpressions.Regex.Replace(searchInput, @"[^\d]", "");

        return await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == searchLower ||
                                                       u.Cedula.ToLower() == searchLower ||
                                                       u.Cedula.ToLower() == withV ||
                                                       (digitsOnly.Length > 0 && (u.Cedula.ToLower() == "v-" + digitsOnly || u.Cedula == digitsOnly)));
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
    public async Task<ActionResult<LoginResultDto>> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Cedula))
        {
            return BadRequest(new { Message = "El usuario es requerido." });
        }

        var searchInput = request.Cedula.Trim();
        var user = await FindUserByCedulaAsync(searchInput);
        var obfCedula = ObfuscateCedula(request.Cedula);

        if (user == null)
        {
            // 8.9-M3: PBKDF2 dummy anti-oráculo de timing (mismo coste que una verificación real).
            PasswordHasher.VerifyPassword(request.Password, _dummyPasswordHash);
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión: Usuario '{obfCedula}' no encontrado.");
            return Unauthorized(new { Message = "Credenciales inválidas." });
        }

        // Account lockout check (H-CORE-3). Responde 401 genérico (anti-enumeración 8B-M3):
        // no se revela si la cuenta existe, está bloqueada ni por cuánto tiempo.
        if (user.LockoutEndUtc.HasValue)
        {
            if (user.LockoutEndUtc.Value > DateTime.UtcNow)
            {
                AppLogger.LogWarn($"[AUTH] Intento de acceso a cuenta bloqueada: Usuario '{obfCedula}'. Bloqueada hasta {user.LockoutEndUtc.Value:O}.");
                return Unauthorized(new { Message = "Credenciales inválidas." });
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
            return Unauthorized(new { Message = "Credenciales inválidas." });
        }

        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión: Usuario '{obfCedula}' no tiene contraseña configurada.");
            return Unauthorized(new { Message = "Credenciales inválidas." });
        }

        bool passwordMatches = !string.IsNullOrWhiteSpace(request.Password) && PasswordHasher.VerifyPassword(request.Password, user.PasswordHash);

        if (!passwordMatches)
        {
            user.AccessFailedCount++;
            // 8.9-M2: el bloqueo NO es re-extendible — una vez fijado LockoutEndUtc no se
            // reinicia el reloj en cada fallo posterior (evita DoS por intentos encadenados).
            if (user.AccessFailedCount >= 5 && (!user.LockoutEndUtc.HasValue || user.LockoutEndUtc.Value <= DateTime.UtcNow))
            {
                user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(15);
                AppLogger.LogWarn($"[AUTH] Usuario '{obfCedula}' alcanzó 5 intentos fallidos. Cuenta bloqueada por 15 minutos.");
            }
            await _db.SaveChangesAsync();
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión para Usuario '{obfCedula}': Contraseña incorrecta (Intento {user.AccessFailedCount}/5).");
            return Unauthorized(new { Message = "Credenciales inválidas." });
        }

        // Reset lockout counters on success
        user.AccessFailedCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(user.SecurityStamp))
        {
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        }
        await _db.SaveChangesAsync();

        if (user.MustChangePassword)
        {
            return StatusCode(403, new LoginResultDto
            {
                RequiresPasswordChange = true,
                Message = "Debe cambiar su contraseña antes de continuar."
            });
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
            // 8.7-B10: Secure según el esquema (la LAN opera por HTTP y el browser descarta
            // toda cookie Secure recibida sin TLS salvo loopback).
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

    /// <summary>
    /// 8.6-B1-revocación / 8.7-B1: el logout rota el SecurityStamp del usuario, de modo que
    /// todo JWT emitido con el stamp anterior queda revocado al siguiente request (stamp mismatch
    /// en SecurityStampValidationMiddleware). La cookie se borra siempre (8.7-B10).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? User.FindFirst("sub")?.Value;
        if (int.TryParse(idClaim, out int uid))
        {
            if (_stampValidator != null)
            {
                await _stampValidator.RevokeUserStampAsync(uid);
            }
            else
            {
                _stampValidator?.InvalidateUserStamp(uid);
            }
        }

        if (Response?.Cookies != null)
        {
            // 8.7-B10: mismo flag Secure del append (Request.IsHttps).
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

    [AllowAnonymous]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Cedula) ||
            string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { Message = "Usuario, contraseña actual y nueva contraseña son requeridos." });
        }

        var searchInput = request.Cedula.Trim();
        var user = await FindUserByCedulaAsync(searchInput);
        if (user == null)
        {
            // 8.9-M3: PBKDF2 dummy anti-oráculo de timing en change-password.
            PasswordHasher.VerifyPassword(request.CurrentPassword, _dummyPasswordHash);
            return Unauthorized(new { Message = "Credenciales inválidas o contraseña actual incorrecta." });
        }

        // 1. Validar si la cuenta está actualmente bloqueada (8B-M3: respuesta 401 genérica, no revelar lockout)
        if (user.LockoutEndUtc.HasValue)
        {
            if (user.LockoutEndUtc.Value > DateTime.UtcNow)
            {
                AppLogger.LogWarn($"[AUTH] Intento de cambio de contraseña denegado: Usuario '{ObfuscateCedula(user.Username)}' bloqueado temporalmente.");
                return Unauthorized(new { Message = "Credenciales inválidas o contraseña actual incorrecta." });
            }
            else
            {
                user.AccessFailedCount = 0;
                user.LockoutEndUtc = null;
            }
        }

        // 2. Validar contraseña actual e incrementar contador de intentos fallidos
        // 8.9-L10: una cuenta inactiva tampoco puede cambiar su contraseña (misma respuesta 401 genérica).
        if (!user.IsActive)
        {
            return Unauthorized(new { Message = "Credenciales inválidas o contraseña actual incorrecta." });
        }

        if (!PasswordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            user.AccessFailedCount++;
            // 8.9-M2: lockout no re-extendible (mismo criterio que Login).
            if (user.AccessFailedCount >= 5 && (!user.LockoutEndUtc.HasValue || user.LockoutEndUtc.Value <= DateTime.UtcNow))
            {
                user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(15);
                AppLogger.LogSecurityAudit($"[ACCOUNT_LOCKED] Usuario={ObfuscateCedula(user.Username)} bloqueado por 15 min tras fallos en change-password.");
            }
            await _db.SaveChangesAsync();
            AppLogger.LogStart($"[AUTH] Intento fallido en change-password para Usuario '{ObfuscateCedula(user.Username)}': Contraseña incorrecta (Intento {user.AccessFailedCount}/5).");
            return Unauthorized(new { Message = "Credenciales inválidas o contraseña actual incorrecta." });
        }

        // 3. Validar la nueva contraseña frente a la política centralizada de seguridad
        var (isPolicyValid, policyError) = _passwordPolicyService.ValidatePassword(request.NewPassword, user.Username);
        if (!isPolicyValid)
        {
            return BadRequest(new { Message = policyError });
        }

        // 4. Actualización atómica/transaccional de credenciales y regeneración de SecurityStamp
        // 8.16-H02: la transacción manual debe vivir DENTRO de CreateExecutionStrategy().ExecuteAsync(),
        // de lo contrario NpgsqlRetryingExecutionStrategy lanza InvalidOperationException en producción.
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
            user.PasswordHash = PasswordHasher.HashPassword(request.NewPassword);
            user.MustChangePassword = false;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await _db.SaveChangesAsync();
            if (tx != null)
            {
                await tx.CommitAsync();
            }
        });

        _stampValidator?.InvalidateUserStamp(user.Id);
        AppLogger.LogSecurityAudit($"[PASSWORD_CHANGED] UserId={user.Id}, Username={ObfuscateCedula(user.Username)}, Timestamp={DateTime.UtcNow:O}");

        // 8.7-B10: revocación de la sesión web (limpieza de cookie pos_jwt) con el mismo
        // flag Secure del append (Request.IsHttps).
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

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> GetMe()
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                   ?? User.FindFirst("sub")?.Value;
        if (!int.TryParse(idClaim, out int currentUserId))
        {
            return Unauthorized();
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId);
        if (user == null || !user.IsActive)
        {
            return Unauthorized();
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
}
