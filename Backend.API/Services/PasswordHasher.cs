namespace Backend.API.Services;

public static class PasswordHasher
{
    public static string HashPassword(string password) => Core.Security.PasswordHasher.HashPassword(password);

    public static bool VerifyPassword(string password, string stored) => Core.Security.PasswordHasher.VerifyPassword(password, stored);
}
