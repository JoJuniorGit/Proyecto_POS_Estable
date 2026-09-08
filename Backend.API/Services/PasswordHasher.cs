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
    // 8B-B6: clamps anti-DoS. Un hash almacenado (p. ej. BD comprometida o corrompida) puede
    // declarar un numero arbitrario de iteraciones o un largo de clave arbitrario: verificar con
    // PBKDF2$1$ haria trivial un ataque de fuerza bruta sobre el hash, y PBKDF2$2147483647$
    // convertiria cada login en un DoS de CPU. Se rechaza fuera de rango (verify = false).
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

            // 8B-B6: clamp de iteraciones (rechaza coste degradado o DoS por coste inflado).
            if (iterations < MinIterations || iterations > MaxIterations)
            {
                return false;
            }

            try
            {
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] expected = Convert.FromBase64String(parts[3]);
                // 8B-B6: clamp del largo de clave (acota memoria/tiempo de la derivacion).
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

        // Strict: Reject any non-PBKDF2 formatted hash. Plain-text fallback is strictly eliminated (H-API-23).
        return false;
    }
}
