using System;
using System.Security.Cryptography;

namespace Backend.API.Services;

/// <summary>
/// Password hashing using PBKDF2 (SHA-256) with a per-password salt.
/// Also verifies legacy plain-text hashes (the seeded admin stored the raw seed
/// password before hashing was introduced), upgrading on the next change.
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2$";
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        if (stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || !int.TryParse(parts[1], out int iterations))
            {
                return false;
            }

            try
            {
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] expected = Convert.FromBase64String(parts[3]);
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // Legacy plain-text comparison (seed admin before hashing was introduced).
        return string.Equals(password, stored, StringComparison.Ordinal);
    }
}
