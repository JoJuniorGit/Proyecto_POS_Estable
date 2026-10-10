using System;
using System.Text.Json;
using Core.Entities;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.153 (SEC-05): los campos sensibles de la entidad User no deben salir en el JSON del
// pipeline (System.Text.Json); el resto de la entidad conserva su serializacion normal.
public class UserSerializationTests
{
    private static User BuildUser() => new()
    {
        Id = 7,
        Cedula = "V-12345678",
        Name = "Usuario Sensible",
        Username = "usuario.sensible",
        PasswordHash = "hash-SENSIBLE-XYZ",
        SecurityStamp = "stamp-SENSIBLE-XYZ",
        Role = UserRole.Admin
    };

    [Fact]
    public void Serialize_DoesNotExposePasswordHashOrSecurityStamp()
    {
        string json = JsonSerializer.Serialize(BuildUser()).ToLowerInvariant();

        Assert.DoesNotContain("hash-sensible-xyz", json);
        Assert.DoesNotContain("stamp-sensible-xyz", json);
        Assert.DoesNotContain("passwordhash", json);
        Assert.DoesNotContain("securitystamp", json);
    }

    [Fact]
    public void Serialize_StillExposesNonSensitiveFields()
    {
        string json = JsonSerializer.Serialize(BuildUser());

        Assert.Contains("V-12345678", json);
        Assert.Contains("Usuario Sensible", json);
        Assert.Contains("usuario.sensible", json);
    }
}
