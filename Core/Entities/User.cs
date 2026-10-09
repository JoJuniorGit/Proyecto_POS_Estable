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
    // 8.153 (SEC-05): el hash nunca debe salir en JSON (el pipeline serializa con System.Text.Json).
    [System.Text.Json.Serialization.JsonIgnore]
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Cashier;
    public string? PhoneNumber { get; set; } // For Drivers
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = false;

    // Phase 4: Token Revocation, Lockout & Security Audit
    // 8.153 (SEC-05): el sello de sesión nunca debe salir en JSON (el pipeline serializa con System.Text.Json).
    [System.Text.Json.Serialization.JsonIgnore]
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public int AccessFailedCount { get; set; } = 0;
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
}
