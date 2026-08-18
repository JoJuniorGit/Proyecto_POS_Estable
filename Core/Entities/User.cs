namespace Core.Entities;

public enum UserRole
{
    Admin,
    Cashier,
    Driver
}

public class User : BaseEntity
{
    public string Cedula { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Cashier;
    public string? PhoneNumber { get; set; } // For Drivers
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = false;
}
