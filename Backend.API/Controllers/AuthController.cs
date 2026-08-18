using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Core.DTOs;
using Core.Logging;
using Backend.API.Services;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly SalesDbContext _db;
    private readonly ITokenService _tokenService;

    public AuthController(SalesDbContext db, ITokenService tokenService)
    {
        _db = db;
        _tokenService = tokenService;
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResultDto>> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Cedula))
        {
            return BadRequest(new { Message = "La Cédula es requerida." });
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Cedula == request.Cedula.Trim());

        if (user == null)
        {
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión: Cédula '{request.Cedula}' no encontrada.");
            return NotFound(new { Message = "Usuario no encontrado con esa Cédula." });
        }

        if (!user.IsActive)
        {
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión para Cédula '{request.Cedula}': Usuario inactivo.");
            return Unauthorized(new { Message = "El usuario está inactivo en el sistema." });
        }

        if (string.IsNullOrWhiteSpace(request.Password) || !PasswordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            AppLogger.LogStart($"[AUTH] Intento fallido de inicio de sesión para Cédula '{request.Cedula}': Contraseña incorrecta.");
            return Unauthorized(new { Message = "Contraseña incorrecta." });
        }

        // Auto-upgrade legacy plain-text password to PBKDF2 hash on successful login
        if (!user.PasswordHash.StartsWith("PBKDF2$", StringComparison.Ordinal))
        {
            user.PasswordHash = PasswordHasher.HashPassword(request.Password);
            await _db.SaveChangesAsync();
            AppLogger.LogStart($"[AUTH] Contraseña migrada exitosamente a PBKDF2 para Cédula '{request.Cedula}'.");
        }

        if (user.MustChangePassword)
        {
            return StatusCode(403, new LoginResultDto
            {
                RequiresPasswordChange = true,
                Message = "Debe cambiar su contraseña antes de continuar."
            });
        }

        var token = _tokenService.GenerateToken(user);

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
            Token = token
        });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Cedula) ||
            string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { Message = "Cédula, contraseña actual y nueva contraseña son requeridas." });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Cedula == request.Cedula.Trim());
        if (user == null)
        {
            return NotFound(new { Message = "Usuario no encontrado con esa Cédula." });
        }

        if (!PasswordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            return Unauthorized(new { Message = "La contraseña actual es incorrecta." });
        }

        user.PasswordHash = PasswordHasher.HashPassword(request.NewPassword);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync();

        return Ok(new { Message = "Contraseña actualizada correctamente." });
    }
}
