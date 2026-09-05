namespace Core.Entities;

public enum UserRole
{
    Cashier = 1,
    Manager = 2,
    Admin = 3,
    Driver = 4
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

    // Phase 4: Token Revocation, Lockout & Security Audit
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public int AccessFailedCount { get; set; } = 0;
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
}
