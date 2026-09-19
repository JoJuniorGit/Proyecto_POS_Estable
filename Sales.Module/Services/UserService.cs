using Core.Constants;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Core.Security;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class UserService : IUserService
{
    private readonly SalesDbContext _db;
    private readonly IPasswordPolicyService _passwordPolicyService;

    public UserService(SalesDbContext db, IPasswordPolicyService? passwordPolicyService = null)
    {
        _db = db;
        _passwordPolicyService = passwordPolicyService ?? new Core.Services.PasswordPolicyService();
    }

    public async Task<IEnumerable<UserDto>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Users
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
            .ToListAsync(cancellationToken);
    }

    public async Task<UserDto?> GetUserAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user == null) return null;

        return new UserDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = string.IsNullOrWhiteSpace(user.Name) ? user.FullName : user.Name,
            Role = user.Role,
            IsActive = user.IsActive
        };
    }

    public async Task<string?> GetUserNameByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => string.IsNullOrWhiteSpace(u.Name) ? u.FullName : u.Name)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<UserCreatedDto> CreateUserAsync(CreateUserDto dto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.Cedula) || string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Usuario y Nombre son campos requeridos.");
        }

        var usernameClean = dto.Cedula.Trim();
        var usernameLower = usernameClean.ToLower();
        var existing = await _db.Users.AnyAsync(u => u.Cedula.ToLower() == usernameLower || u.Username.ToLower() == usernameLower, cancellationToken);
        if (existing)
        {
            throw new InvalidOperationException("Ya existe un usuario registrado con ese nombre de usuario.");
        }

        string rawPassword;
        bool mustChange;
        if (!string.IsNullOrWhiteSpace(dto.Password))
        {
            var (isPolicyValid, policyError) = _passwordPolicyService.ValidatePassword(dto.Password, usernameClean);
            if (!isPolicyValid)
            {
                throw new ArgumentException(policyError ?? "La contraseña no cumple con la política de seguridad.");
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
            PasswordHash = PasswordHasher.HashPassword(rawPassword),
            IsActive = true,
            MustChangePassword = mustChange,
            SecurityStamp = Guid.NewGuid().ToString("N")
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        return new UserCreatedDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = user.Name,
            Role = user.Role,
            IsActive = user.IsActive,
            MustChangePassword = mustChange,
            TemporaryPassword = mustChange ? rawPassword : null
        };
    }

    public async Task<(UserDto User, bool CredentialsOrRoleChanged)> UpdateUserAsync(int id, UpdateUserDto dto, int? currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync(new object[] { id }, cancellationToken);
        if (user == null) throw new KeyNotFoundException("Usuario no encontrado.");

        bool isMainAdmin = SecurityConstants.IsRootAdmin(user.Cedula, user.Username);
        if (isMainAdmin)
        {
            if (!dto.IsActive)
            {
                throw new InvalidOperationException("El Administrador principal del sistema no puede ser desactivado.");
            }
            if (dto.Role != UserRole.Admin)
            {
                throw new InvalidOperationException("El Administrador principal del sistema no puede cambiar de rol.");
            }
            if (!string.Equals(dto.Cedula.Trim(), user.Cedula, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("El Administrador principal del sistema no puede cambiar su cédula o nombre de usuario.");
            }
        }

        if (currentUserId.HasValue && id == currentUserId.Value && !dto.IsActive)
        {
            throw new InvalidOperationException("No puede desactivar su propia cuenta de usuario en sesión.");
        }

        var usernameClean = dto.Cedula.Trim();
        var usernameLower = usernameClean.ToLower();
        var existingUser = await _db.Users.AnyAsync(u => (u.Cedula.ToLower() == usernameLower || u.Username.ToLower() == usernameLower) && u.Id != id, cancellationToken);
        if (existingUser)
        {
            throw new InvalidOperationException("El nombre de usuario especificado ya pertenece a otro usuario.");
        }

        bool credentialsOrRoleChanged = false;
        if (!string.IsNullOrWhiteSpace(dto.Password))
        {
            var (isPolicyValid, policyError) = _passwordPolicyService.ValidatePassword(dto.Password, user.Username);
            if (!isPolicyValid)
            {
                throw new ArgumentException(policyError ?? "La contraseña no cumple con la política de seguridad.");
            }
            user.PasswordHash = PasswordHasher.HashPassword(dto.Password.Trim());
            user.MustChangePassword = false;
            credentialsOrRoleChanged = true;
        }

        if (user.Role != dto.Role || user.IsActive != (isMainAdmin ? true : dto.IsActive))
        {
            credentialsOrRoleChanged = true;
        }

        user.Cedula = isMainAdmin ? user.Cedula : usernameClean;
        user.Username = isMainAdmin ? user.Username : usernameClean;
        user.Name = dto.Name.Trim();
        user.FullName = dto.Name.Trim();
        user.Role = isMainAdmin ? UserRole.Admin : dto.Role;
        user.IsActive = isMainAdmin ? true : dto.IsActive;

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
            if (credentialsOrRoleChanged)
            {
                user.SecurityStamp = Guid.NewGuid().ToString("N");
            }
            await _db.SaveChangesAsync(cancellationToken);
            if (tx != null)
            {
                await tx.CommitAsync(cancellationToken);
            }
        });

        var userDto = new UserDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = user.Name,
            Role = user.Role,
            IsActive = user.IsActive
        };

        return (userDto, credentialsOrRoleChanged);
    }

    public async Task SoftDeleteUserAsync(int id, int? currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync(new object[] { id }, cancellationToken);
        if (user == null) throw new KeyNotFoundException("Usuario no encontrado.");

        if (SecurityConstants.IsRootAdmin(user.Cedula, user.Username))
        {
            throw new InvalidOperationException("El Administrador principal del sistema no puede ser desactivado.");
        }

        if (currentUserId.HasValue && id == currentUserId.Value)
        {
            throw new InvalidOperationException("No puede desactivar su propia cuenta de usuario en sesión.");
        }

        user.IsActive = false;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReactivateUserAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync(new object[] { id }, cancellationToken);
        if (user == null) throw new KeyNotFoundException("Usuario no encontrado.");

        user.IsActive = true;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task HardDeleteUserAsync(int id, int? currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync(new object[] { id }, cancellationToken);
        if (user == null) throw new KeyNotFoundException("Usuario no encontrado.");

        if (SecurityConstants.IsRootAdmin(user.Cedula, user.Username))
        {
            throw new InvalidOperationException("El Administrador principal del sistema no puede ser eliminado.");
        }

        if (currentUserId.HasValue && id == currentUserId.Value)
        {
            throw new InvalidOperationException("No puede eliminar su propia cuenta de usuario en sesión.");
        }

        await _db.Sales
            .Where(s => s.CashierId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(s => s.CashierId, (int?)null), cancellationToken);

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> UnlockUserAsync(int id, int? adminId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync(new object[] { id }, cancellationToken);
        if (user == null) throw new KeyNotFoundException("Usuario no encontrado.");

        if (!user.IsActive)
        {
            throw new InvalidOperationException("No se puede desbloquear una cuenta de usuario inactiva o deshabilitada.");
        }

        user.AccessFailedCount = 0;
        user.LockoutEndUtc = null;
        await _db.SaveChangesAsync(cancellationToken);

        return user.Username;
    }

    public async Task<ResetTemporaryPasswordResponseDto> ResetTemporaryPasswordAsync(int id, int? adminId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync(new object[] { id }, cancellationToken);
        if (user == null) throw new KeyNotFoundException("Usuario no encontrado.");

        if (!user.IsActive)
        {
            throw new InvalidOperationException("No se puede restablecer la contraseña de un usuario inactivo o deshabilitado.");
        }

        var temporaryPassword = _passwordPolicyService.GenerateSecureTemporaryPassword(12);

        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
            user.PasswordHash = PasswordHasher.HashPassword(temporaryPassword);
            user.MustChangePassword = true;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
            await _db.SaveChangesAsync(cancellationToken);
            if (tx != null)
            {
                await tx.CommitAsync(cancellationToken);
            }
        });

        return new ResetTemporaryPasswordResponseDto
        {
            UserId = user.Id,
            TemporaryPassword = temporaryPassword,
            Message = "Contraseña temporal regenerada exitosamente. Debe ser cambiada en el próximo inicio de sesión."
        };
    }
}
