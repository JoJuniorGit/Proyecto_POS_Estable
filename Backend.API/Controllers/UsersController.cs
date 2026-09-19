using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Services;
using Core.Logging;
using Core.Interfaces;
using Sales.Module.Data;
using Sales.Module.Services;

namespace Backend.API.Controllers;

[Authorize(Roles = "Admin")]
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IPasswordPolicyService _passwordPolicyService;
    private readonly ISecurityStampValidator? _stampValidator;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public UsersController(
        IUserService userService, 
        IPasswordPolicyService? passwordPolicyService = null,
        ISecurityStampValidator? stampValidator = null)
    {
        _userService = userService;
        _passwordPolicyService = passwordPolicyService ?? new Core.Services.PasswordPolicyService();
        _stampValidator = stampValidator;
    }

    public UsersController(
        SalesDbContext db, 
        IPasswordPolicyService? passwordPolicyService = null,
        ISecurityStampValidator? stampValidator = null)
        : this(new UserService(db, passwordPolicyService), passwordPolicyService, stampValidator)
    {
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
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = await _userService.GetUsersAsync(cancellationToken);
        return Ok(users);
    }

    [NonAction]
    public Task<ActionResult<IEnumerable<UserDto>>> GetUsers() => GetUsersAsync();

    [HttpGet("{id}")]
    public async Task<ActionResult<UserDto>> GetUserAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = await _userService.GetUserAsync(id, cancellationToken);
        if (user == null) return this.ApiNotFound("Usuario no encontrado.");
        return Ok(user);
    }

    [NonAction]
    public Task<ActionResult<UserDto>> GetUser(int id) => GetUserAsync(id);

    [HttpPost]
    public async Task<ActionResult<UserCreatedDto>> CreateUserAsync([FromBody] CreateUserDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var created = await _userService.CreateUserAsync(dto, cancellationToken);
            return CreatedAtAction(nameof(GetUserAsync), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult<UserCreatedDto>> CreateUser(CreateUserDto dto) => CreateUserAsync(dto);

    [HttpPut("{id}")]
    public async Task<ActionResult<UserDto>> UpdateUserAsync(int id, [FromBody] UpdateUserDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var (user, credentialsOrRoleChanged) = await _userService.UpdateUserAsync(id, dto, GetCurrentUserId(), cancellationToken);
            if (credentialsOrRoleChanged)
            {
                _stampValidator?.InvalidateUserStamp(user.Id);
                AppLogger.LogSecurityAudit($"[AUDIT_SECURITY_STAMP_RESET] UserId={user.Id}, Username={user.Cedula}, Reason=UserUpdated");
            }
            return Ok(user);
        }
        catch (KeyNotFoundException)
        {
            return this.ApiNotFound("Usuario no encontrado.");
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult<UserDto>> UpdateUser(int id, UpdateUserDto dto) => UpdateUserAsync(id, dto);

    [HttpDelete("{id}")]
    public async Task<ActionResult> SoftDeleteUserAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            await _userService.SoftDeleteUserAsync(id, GetCurrentUserId(), cancellationToken);
            _stampValidator?.InvalidateUserStamp(id);
            AppLogger.LogSecurityAudit($"[AUDIT_SECURITY_STAMP_RESET] UserId={id}, Reason=UserDeactivated");
            return Ok(new { Message = "Usuario desactivado exitosamente." });
        }
        catch (KeyNotFoundException)
        {
            return this.ApiNotFound("Usuario no encontrado.");
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult> SoftDeleteUser(int id) => SoftDeleteUserAsync(id);

    [HttpPost("{id}/reactivate")]
    public async Task<ActionResult> ReactivateUserAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            await _userService.ReactivateUserAsync(id, cancellationToken);
            _stampValidator?.InvalidateUserStamp(id);
            AppLogger.LogSecurityAudit($"[AUDIT_SECURITY_STAMP_RESET] UserId={id}, Reason=UserReactivated");
            return Ok(new { Message = "Usuario reactivado exitosamente." });
        }
        catch (KeyNotFoundException)
        {
            return this.ApiNotFound("Usuario no encontrado.");
        }
    }

    [NonAction]
    public Task<ActionResult> ReactivateUser(int id) => ReactivateUserAsync(id);

    [HttpDelete("{id}/permanent")]
    public async Task<ActionResult> HardDeleteUserAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            await _userService.HardDeleteUserAsync(id, GetCurrentUserId(), cancellationToken);
            _stampValidator?.InvalidateUserStamp(id);
            AppLogger.LogSecurityAudit($"[AUDIT_SECURITY_STAMP_RESET] UserId={id}, Reason=UserPermanentlyDeleted");
            return Ok(new { Message = "Usuario eliminado permanentemente." });
        }
        catch (KeyNotFoundException)
        {
            return this.ApiNotFound("Usuario no encontrado.");
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult> HardDeleteUser(int id) => HardDeleteUserAsync(id);

    [HttpPost("{id}/unlock")]
    public async Task<ActionResult> UnlockUserAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var username = await _userService.UnlockUserAsync(id, GetCurrentUserId(), cancellationToken);
            var adminId = GetCurrentUserId();
            AppLogger.LogSecurityAudit($"[USER_UNLOCKED] TargetUserId={id}, Username={username}, UnlockedBy={adminId}, Timestamp={DateTime.UtcNow:O}");
            return Ok(new { Message = $"Cuenta de {username} desbloqueada exitosamente." });
        }
        catch (KeyNotFoundException)
        {
            return this.ApiNotFound("Usuario no encontrado.");
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult> UnlockUser(int id) => UnlockUserAsync(id);

    [HttpPost("{id}/reset-temporary-password")]
    public async Task<ActionResult<ResetTemporaryPasswordResponseDto>> ResetTemporaryPasswordAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await _userService.ResetTemporaryPasswordAsync(id, GetCurrentUserId(), cancellationToken);
            _stampValidator?.InvalidateUserStamp(id);
            var adminId = GetCurrentUserId();
            AppLogger.LogSecurityAudit($"[TEMP_PASSWORD_RESET] TargetUserId={id}, ResetBy={adminId}, Timestamp={DateTime.UtcNow:O}");
            return Ok(res);
        }
        catch (KeyNotFoundException)
        {
            return this.ApiNotFound("Usuario no encontrado.");
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult<ResetTemporaryPasswordResponseDto>> ResetTemporaryPassword(int id) => ResetTemporaryPasswordAsync(id);
}
