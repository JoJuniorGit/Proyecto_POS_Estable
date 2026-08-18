using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Core.DTOs;
using Backend.API.Services;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly SalesDbContext _db;

    public AuthController(SalesDbContext db)
    {
        _db = db;
    }

    [HttpPost("login")]
    public async Task<ActionResult<UserDto>> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Cedula))
        {
            return BadRequest(new { Message = "La Cédula es requerida." });
        }

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Cedula == request.Cedula.Trim());

        if (user == null)
        {
            return NotFound(new { Message = "Usuario no encontrado con esa Cédula." });
        }

        if (!user.IsActive)
        {
            return Unauthorized(new { Message = "El usuario está inactivo en el sistema." });
        }

        if (user.MustChangePassword)
        {
            return StatusCode(403, new
            {
                RequiresPasswordChange = true,
                Message = "Debe cambiar su contraseña antes de continuar."
            });
        }

        var dto = new UserDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = string.IsNullOrWhiteSpace(user.Name) ? user.FullName : user.Name,
            Role = user.Role,
            IsActive = user.IsActive
        };

        return Ok(dto);
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
