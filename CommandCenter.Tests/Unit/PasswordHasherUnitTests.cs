using System;
using Backend.API.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PasswordHasherUnitTests
{
    [Fact]
    public void HashPassword_ProducesPbkdf2FormattedString()
    {
        string hash = PasswordHasher.HashPassword("AdminPass2026!");

        Assert.NotNull(hash);
        Assert.StartsWith("PBKDF2$100000$", hash);
        var parts = hash.Split('$');
        Assert.Equal(4, parts.Length);
    }

    [Fact]
    public void VerifyPassword_WithMatchingPassword_ReturnsTrue()
    {
        string password = "SecretPassword123#";
        string hash = PasswordHasher.HashPassword(password);

        bool isValid = PasswordHasher.VerifyPassword(password, hash);

        Assert.True(isValid);
    }

    [Fact]
    public void VerifyPassword_WithWrongPassword_ReturnsFalse()
    {
        string hash = PasswordHasher.HashPassword("CorrectPassword");

        bool isValid = PasswordHasher.VerifyPassword("WrongPassword", hash);

        Assert.False(isValid);
    }

    [Fact]
    public void VerifyPassword_IsCaseSensitive()
    {
        string hash = PasswordHasher.HashPassword("Password123");

        Assert.True(PasswordHasher.VerifyPassword("Password123", hash));
        Assert.False(PasswordHasher.VerifyPassword("password123", hash));
        Assert.False(PasswordHasher.VerifyPassword("PASSWORD123", hash));
    }

    [Fact]
    public void VerifyPassword_WithLegacyPlainText_IsStrictlyRejected()
    {
        string plainTextSeed = "InitialAdminPass";

        // H-API-23: Plain-text stored passwords without PBKDF2$ prefix are strictly rejected
        bool isValid = PasswordHasher.VerifyPassword("InitialAdminPass", plainTextSeed);
        bool isInvalid = PasswordHasher.VerifyPassword("WrongPass", plainTextSeed);

        Assert.False(isValid);
        Assert.False(isInvalid);
    }

    [Fact]
    public void VerifyPassword_WithEmptyOrMalformedHash_ReturnsFalseSafely()
    {
        Assert.False(PasswordHasher.VerifyPassword("any", ""));
        Assert.False(PasswordHasher.VerifyPassword("any", "   "));
        Assert.False(PasswordHasher.VerifyPassword("any", "PBKDF2$not_an_int$bad_salt$bad_hash"));
        Assert.False(PasswordHasher.VerifyPassword("any", "PBKDF2$100000$not_valid_base64!$bad_hash!"));
    }

    [Fact]
    public void VerifyPassword_WithDegradedIterations_IsRejected()
    {
        // 8B-B6: un hash que declara pocas iteraciones degradaria el coste de derivacion
        // (fuerza bruta trivial); debe rechazarse sin derivar.
        string hash = PasswordHasher.HashPassword("Secret123#");
        var parts = hash.Split('$');
        string downgraded = $"PBKDF2$1${parts[2]}${parts[3]}";

        Assert.False(PasswordHasher.VerifyPassword("Secret123#", downgraded));
    }

    [Fact]
    public void VerifyPassword_WithInflatedIterations_IsRejected()
    {
        // 8B-B6: un hash que declara millones de iteraciones convertiria cada login en un
        // DoS de CPU; debe rechazarse sin derivar.
        string hash = PasswordHasher.HashPassword("Secret123#");
        var parts = hash.Split('$');
        string inflated = $"PBKDF2$2147483647${parts[2]}${parts[3]}";

        Assert.False(PasswordHasher.VerifyPassword("Secret123#", inflated));
    }

    [Fact]
    public void VerifyPassword_WithOutOfRangeKeyLength_IsRejected()
    {
        // 8B-B6: clave fuera de 16..64 bytes debe rechazarse sin derivar.
        string hash = PasswordHasher.HashPassword("Secret123#");
        var parts = hash.Split('$');
        string tinyKey = $"PBKDF2$100000${parts[2]}${Convert.ToBase64String(new byte[1])}";

        Assert.False(PasswordHasher.VerifyPassword("Secret123#", tinyKey));
    }
}
