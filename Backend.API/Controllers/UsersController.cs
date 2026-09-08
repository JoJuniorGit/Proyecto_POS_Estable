using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Core.Entities;
using Core.DTOs;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Services;
using Core.Logging;

using Core.Interfaces;
using Core.Constants;

namespace Backend.API.Controllers;

[Authorize(Roles = "Admin")]
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly SalesDbContext _db;
    private readonly IPasswordPolicyService _passwordPolicyService;
    private readonly ISecurityStampValidator? _stampValidator;

    public UsersController(
        SalesDbContext db, 
        IPasswordPolicyService? passwordPolicyService = null,
        ISecurityStampValidator? stampValidator = null)
    {
        _db = db;
        _passwordPolicyService = passwordPolicyService ?? new Core.Services.PasswordPolicyService();
        _stampValidator = stampValidator;
    }

    private int? GetCurrentUserId()
    {
        var idClaim = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                   ?? User?.FindFirst("sub")?.Value;
        if (int.TryParse(idClaim, out int currentUserId))
        {
            return currentUserId;
        }
        return null;
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
    public async Task<ActionResult<UserCreatedDto>> CreateUser([FromBody] CreateUserDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Cedula) || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new { Message = "Usuario y Nombre son campos requeridos." });
        }

        var usernameClean = dto.Cedula.Trim();
        var usernameLower = usernameClean.ToLower();
        var existing = await _db.Users.AnyAsync(u => u.Cedula.ToLower() == usernameLower || u.Username.ToLower() == usernameLower);
        if (existing)
        {
            return BadRequest(new { Message = "Ya existe un usuario registrado con ese nombre de usuario." });
        }

        string rawPassword;
        bool mustChange;
        if (!string.IsNullOrWhiteSpace(dto.Password))
        {
            var (isPolicyValid, policyError) = _passwordPolicyService.ValidatePassword(dto.Password, usernameClean);
            if (!isPolicyValid)
            {
                return BadRequest(new { Message = policyError });
            }
            rawPassword = dto.Password.Trim();
            mustChange = false;
        }
        else
        {
            rawPassword = _passwordPolicyService.GenerateSecureTemporaryPassword(12);
            mustChange = true;
        }

        var user = new User
        {
            Cedula = usernameClean,
            Name = dto.Name.Trim(),
            FullName = dto.Name.Trim(),
            Username = usernameClean,
            Role = dto.Role,
            PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(rawPassword),
            IsActive = true,
            MustChangePassword = mustChange,
            SecurityStamp = Guid.NewGuid().ToString("N")
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, new UserCreatedDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = user.Name,
            Role = user.Role,
            IsActive = user.IsActive,
            MustChangePassword = mustChange,
            TemporaryPassword = mustChange ? rawPassword : null
        });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<UserDto>> UpdateUser(int id, [FromBody] UpdateUserDto dto)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        bool isMainAdmin = SecurityConstants.IsRootAdmin(user.Cedula, user.Username);
        if (isMainAdmin && !dto.IsActive)
        {
            return BadRequest(new { Message = "El Administrador principal del sistema no puede ser desactivado." });
        }

        var currentUserId = GetCurrentUserId();
        if (currentUserId.HasValue && id == currentUserId.Value && !dto.IsActive)
        {
            return BadRequest(new { Message = "No puede desactivar su propia cuenta de usuario en sesión." });
        }

        var usernameClean = dto.Cedula.Trim();
        var usernameLower = usernameClean.ToLower();
        var existingUser = await _db.Users.AnyAsync(u => (u.Cedula.ToLower() == usernameLower || u.Username.ToLower() == usernameLower) && u.Id != id);
        if (existingUser)
        {
            return BadRequest(new { Message = "El nombre de usuario especificado ya pertenece a otro usuario." });
        }

        bool credentialsOrRoleChanged = false;
        if (!string.IsNullOrWhiteSpace(dto.Password))
        {
            var (isPolicyValid, policyError) = _passwordPolicyService.ValidatePassword(dto.Password, user.Username);
            if (!isPolicyValid)
            {
                return BadRequest(new { Message = policyError });
            }
            user.PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(dto.Password.Trim());
            user.MustChangePassword = false;
            credentialsOrRoleChanged = true;
        }

        if (user.Role != dto.Role || user.IsActive != (isMainAdmin ? true : dto.IsActive))
        {
            credentialsOrRoleChanged = true;
        }

        user.Cedula = usernameClean;
        user.Username = usernameClean;
        user.Name = dto.Name.Trim();
        user.FullName = dto.Name.Trim();
        user.Role = dto.Role;
        user.IsActive = isMainAdmin ? true : dto.IsActive;

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
            if (credentialsOrRoleChanged)
            {
                user.SecurityStamp = Guid.NewGuid().ToString("N");
            }
            await _db.SaveChangesAsync();
            if (tx != null)
            {
                await tx.CommitAsync();
            }
        });

        if (credentialsOrRoleChanged)
        {
            _stampValidator?.InvalidateUserStamp(user.Id);
            AppLogger.LogSecurityAudit($"[AUDIT_SECURITY_STAMP_RESET] UserId={user.Id}, Username={user.Username}, Reason=UserUpdated");
        }

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

        if (SecurityConstants.IsRootAdmin(user.Cedula, user.Username))
        {
            return BadRequest(new { Message = "El Administrador principal del sistema no puede ser desactivado." });
        }

        var currentUserId = GetCurrentUserId();
        if (currentUserId.HasValue && id == currentUserId.Value)
        {
            return BadRequest(new { Message = "No puede desactivar su propia cuenta de usuario en sesión." });
        }

        user.IsActive = false;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        _stampValidator?.InvalidateUserStamp(user.Id);
        AppLogger.LogSecurityAudit($"[AUDIT_SECURITY_STAMP_RESET] UserId={user.Id}, Username={user.Username}, Reason=UserDeactivated");
        await _db.SaveChangesAsync();

        return Ok(new { Message = "Usuario desactivado exitosamente." });
    }

    [HttpPost("{id}/reactivate")]
    public async Task<ActionResult> ReactivateUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        user.IsActive = true;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        _stampValidator?.InvalidateUserStamp(user.Id);
        AppLogger.LogSecurityAudit($"[AUDIT_SECURITY_STAMP_RESET] UserId={user.Id}, Username={user.Username}, Reason=UserReactivated");
        await _db.SaveChangesAsync();

        return Ok(new { Message = "Usuario reactivado exitosamente." });
    }

    [HttpDelete("{id}/permanent")]
    public async Task<ActionResult> HardDeleteUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        if (SecurityConstants.IsRootAdmin(user.Cedula, user.Username))
        {
            return BadRequest(new { Message = "El Administrador principal del sistema no puede ser eliminado." });
        }

        var currentUserId = GetCurrentUserId();
        if (currentUserId.HasValue && id == currentUserId.Value)
        {
            return BadRequest(new { Message = "No puede eliminar su propia cuenta de usuario en sesión." });
        }

        var sales = await _db.Sales.Where(s => s.CashierId == id).ToListAsync();
        foreach (var s in sales)
        {
            s.CashierId = null;
        }

        _stampValidator?.InvalidateUserStamp(id);
        AppLogger.LogSecurityAudit($"[AUDIT_SECURITY_STAMP_RESET] UserId={id}, Username={user.Username}, Reason=UserPermanentlyDeleted");

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();

        return Ok(new { Message = "Usuario eliminado permanentemente." });
    }

    [HttpPost("{id}/unlock")]
    public async Task<ActionResult> UnlockUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound(new { Message = "Usuario no encontrado." });

        if (!user.IsActive)
        {
            return BadRequest(new { Message = "No se puede desbloquear una cuenta de usuario inactiva o deshabilitada." });
        }

        user.AccessFailedCount = 0;
        user.LockoutEndUtc = null;
        await _db.SaveChangesAsync();

        var adminId = GetCurrentUserId();
        AppLogger.LogSecurityAudit($"[USER_UNLOCKED] TargetUserId={user.Id}, Username={user.Username}, UnlockedBy={adminId}, Timestamp={DateTime.UtcNow:O}");

        return Ok(new { Message = $"Cuenta de {user.Username} desbloqueada exitosamente." });
    }

    [HttpPost("{id}/reset-temporary-password")]
    public async Task<ActionResult<ResetTemporaryPasswordResponseDto>> ResetTemporaryPassword(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound(new { Message = "Usuario no encontrado." });

        if (!user.IsActive)
        {
            return BadRequest(new { Message = "No se puede restablecer la contraseña de un usuario inactivo o deshabilitado." });
        }

        var temporaryPassword = _passwordPolicyService.GenerateSecureTemporaryPassword(12);

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
            user.PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(temporaryPassword);
            user.MustChangePassword = true;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
            await _db.SaveChangesAsync();
            if (tx != null)
            {
                await tx.CommitAsync();
            }
        });

        _stampValidator?.InvalidateUserStamp(user.Id);

        var adminId = GetCurrentUserId();
        AppLogger.LogSecurityAudit($"[TEMP_PASSWORD_RESET] TargetUserId={user.Id}, Username={user.Username}, ResetBy={adminId}, Timestamp={DateTime.UtcNow:O}");

        return Ok(new ResetTemporaryPasswordResponseDto
        {
            UserId = user.Id,
            TemporaryPassword = temporaryPassword,
            Message = "Contraseña temporal regenerada exitosamente. Debe ser cambiada en el próximo inicio de sesión."
        });
    }
}
