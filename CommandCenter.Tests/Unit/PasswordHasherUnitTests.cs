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
    public void VerifyPassword_WithLegacyPlainText_MatchesCorrectly()
    {
        string plainTextSeed = "InitialAdminPass";

        bool isValid = PasswordHasher.VerifyPassword("InitialAdminPass", plainTextSeed);
        bool isInvalid = PasswordHasher.VerifyPassword("WrongPass", plainTextSeed);

        Assert.True(isValid);
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
}
