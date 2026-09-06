using System.ComponentModel.DataAnnotations;
using Core.Entities;

namespace Core.DTOs;

public class LoginRequest
{
    [Required]
    public string Cedula { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Plataforma de origen ("Web" o "Desktop"). Permite diferenciar el canal de entrega del token (Cookies vs Bearer).
    /// </summary>
    public string? Platform { get; set; }
}

public class ChangePasswordRequest
{
    [Required]
    public string Cedula { get; set; } = string.Empty;

    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    public string NewPassword { get; set; } = string.Empty;
}

public class LoginResultDto
{
    public UserDto? User { get; set; }
    public bool RequiresPasswordChange { get; set; }
    public string? Message { get; set; }
    public string? Token { get; set; }
}

public class UserDto
{
    public int Id { get; set; }
    public string Cedula { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public bool IsActive { get; set; }
}

public class CreateUserDto
{
    [Required]
    public string Cedula { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Password { get; set; }

    public UserRole Role { get; set; } = UserRole.Cashier;
}

public class UpdateUserDto
{
    [Required]
    public string Cedula { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Password { get; set; }

    public UserRole Role { get; set; } = UserRole.Cashier;

    public bool IsActive { get; set; } = true;
}

public class UserCreatedDto : UserDto
{
    public string? TemporaryPassword { get; set; }
    public bool MustChangePassword { get; set; }
}

public class ResetTemporaryPasswordResponseDto
{
    public int UserId { get; set; }
    public string TemporaryPassword { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

