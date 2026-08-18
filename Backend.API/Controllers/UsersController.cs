using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Core.Entities;
using Core.DTOs;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly SalesDbContext _db;

    public UsersController(SalesDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers()
    {
        var users = await _db.Users
            .AsNoTracking()
            .OrderBy(u => u.Name)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Cedula = u.Cedula,
                Name = string.IsNullOrWhiteSpace(u.Name) ? u.FullName : u.Name,
                Role = u.Role,
                IsActive = u.IsActive
            })
            .ToListAsync();

        return Ok(users);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserDto>> GetUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        return Ok(new UserDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = string.IsNullOrWhiteSpace(user.Name) ? user.FullName : user.Name,
            Role = user.Role,
            IsActive = user.IsActive
        });
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Cedula) || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new { Message = "Cédula y Nombre son campos requeridos." });
        }

        var cedulaClean = dto.Cedula.Trim();
        var existing = await _db.Users.AnyAsync(u => u.Cedula == cedulaClean);
        if (existing)
        {
            return BadRequest(new { Message = "Ya existe un usuario registrado con esa Cédula." });
        }

        var user = new User
        {
            Cedula = cedulaClean,
            Name = dto.Name.Trim(),
            FullName = dto.Name.Trim(),
            Username = cedulaClean,
            Role = dto.Role,
            IsActive = true
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, new UserDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = user.Name,
            Role = user.Role,
            IsActive = user.IsActive
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<UserDto>> UpdateUser(int id, [FromBody] UpdateUserDto dto)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        bool isMainAdmin = user.Cedula == "V-00000000" || user.Cedula == "V-12345678" || user.Username == "Admin";
        if (isMainAdmin && !dto.IsActive)
        {
            return BadRequest(new { Message = "El Administrador principal del sistema no puede ser desactivado." });
        }

        if (Request.Headers.TryGetValue("X-User-Id", out var userIdHeader) && int.TryParse(userIdHeader, out int currentUserId))
        {
            if (id == currentUserId && !dto.IsActive)
            {
                return BadRequest(new { Message = "No puede desactivar su propia cuenta de usuario en sesión." });
            }
        }

        var cedulaClean = dto.Cedula.Trim();
        var existingCedula = await _db.Users.AnyAsync(u => u.Cedula == cedulaClean && u.Id != id);
        if (existingCedula)
        {
            return BadRequest(new { Message = "La Cédula especificada ya pertenece a otro usuario." });
        }

        user.Cedula = cedulaClean;
        user.Name = dto.Name.Trim();
        user.FullName = dto.Name.Trim();
        user.Role = dto.Role;
        user.IsActive = isMainAdmin ? true : dto.IsActive;

        await _db.SaveChangesAsync();

        return Ok(new UserDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = user.Name,
            Role = user.Role,
            IsActive = user.IsActive
        });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> SoftDeleteUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        if (user.Cedula == "V-00000000" || user.Username == "Admin")
        {
            return BadRequest(new { Message = "El Administrador principal del sistema no puede ser desactivado." });
        }

        if (Request.Headers.TryGetValue("X-User-Id", out var userIdHeader) && int.TryParse(userIdHeader, out int currentUserId))
        {
            if (id == currentUserId)
            {
                return BadRequest(new { Message = "No puede desactivar su propia cuenta de usuario en sesión." });
            }
        }

        user.IsActive = false;
        await _db.SaveChangesAsync();

        return Ok(new { Message = "Usuario desactivado exitosamente." });
    }

    [HttpPost("{id}/reactivate")]
    public async Task<ActionResult> ReactivateUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        user.IsActive = true;
        await _db.SaveChangesAsync();

        return Ok(new { Message = "Usuario reactivado exitosamente." });
    }

    [HttpDelete("{id}/permanent")]
    public async Task<ActionResult> HardDeleteUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        if (user.Cedula == "V-00000000" || user.Username == "Admin")
        {
            return BadRequest(new { Message = "El Administrador principal del sistema no puede ser eliminado." });
        }

        if (Request.Headers.TryGetValue("X-User-Id", out var userIdHeader) && int.TryParse(userIdHeader, out int currentUserId))
        {
            if (id == currentUserId)
            {
                return BadRequest(new { Message = "No puede eliminar su propia cuenta de usuario en sesión." });
            }
        }

        var sales = await _db.Sales.Where(s => s.CashierId == id).ToListAsync();
        foreach (var s in sales)
        {
            s.CashierId = null;
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();

        return Ok(new { Message = "Usuario eliminado permanentemente." });
    }
}
