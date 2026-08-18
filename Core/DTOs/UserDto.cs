using System.ComponentModel.DataAnnotations;
using Core.Entities;

namespace Core.DTOs;

public class LoginRequest
{
    [Required]
    public string Cedula { get; set; } = string.Empty;
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

    public UserRole Role { get; set; } = UserRole.Cashier;
}

public class UpdateUserDto
{
    [Required]
    public string Cedula { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Cashier;

    public bool IsActive { get; set; } = true;
}
