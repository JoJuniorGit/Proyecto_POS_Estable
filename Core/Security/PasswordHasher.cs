using System;
using System.Security.Cryptography;

namespace Core.Security;

public static class PasswordHasher
{
    private const string Prefix = "PBKDF2$";
    // 8.204: subida a la recomendación OWASP 2026 para PBKDF2-SHA256. Los hashes existentes
    // conservan su contador embebido y se rehashean en el próximo login exitoso.
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int MinIterations = 10_000;
    private const int MaxIterations = 2_000_000;
    private const int MinKeyBytes = 16;
    private const int MaxKeyBytes = 64;

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// 8.204: true cuando el hash fue emitido con menos iteraciones que la política actual (o el
    /// formato es inválido/legado). El llamador debe rehashear la contraseña en el próximo login.
    /// </summary>
    public static bool NeedsRehash(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return true;
        }

        var parts = stored.Split('$');
        return parts.Length != 4
            || !int.TryParse(parts[1], out int iterations)
            || iterations < Iterations;
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

            if (iterations < MinIterations || iterations > MaxIterations)
            {
                return false;
            }

            try
            {
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] expected = Convert.FromBase64String(parts[3]);
                if (expected.Length < MinKeyBytes || expected.Length > MaxKeyBytes)
                {
                    return false;
                }
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return false;
    }
}
