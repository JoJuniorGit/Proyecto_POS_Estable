using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Constants;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T1 (S1/S2): contrato, defaults, normalización ISO 4217, validación con mensajes exactos,
// atomicidad de las cinco claves y lectura tolerante a perfiles corruptos.
public class MonetaryProfileServiceTests
{
    private const string UndefinedModeMessage = "El modo monetario debe ser Single o Dual.";
    private const string MalformedPrimaryCodeMessage = "El código de la moneda principal debe ser un código ISO 4217 de tres letras.";
    private const string UnsupportedCodeMessage = "El código '{0}' no es un código ISO 4217 reconocido.";
    private const string PrimarySymbolMessage = "El símbolo de la moneda principal es obligatorio y no puede superar los 8 caracteres.";
    private const string ControlCharacterMessage = "El símbolo de la moneda no puede contener caracteres de control.";
    private const string SingleWithSecondaryMessage = "En modo Single no se debe configurar una moneda secundaria.";
    private const string DualWithoutSecondaryMessage = "En modo Dual se requiere el código de la moneda secundaria.";
    private const string MalformedSecondaryCodeMessage = "El código de la moneda secundaria debe ser un código ISO 4217 de tres letras.";
    private const string SameCurrencyMessage = "La moneda secundaria no puede ser igual a la principal.";
    private const string SecondarySymbolMessage = "El símbolo de la moneda secundaria es obligatorio y no puede superar los 8 caracteres.";

    private static InventoryDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private static MonetaryProfileService CreateService(InventoryDbContext db)
        => new(new SystemSettingsService(db));

    private static MonetaryProfileDto ValidDualProfile() => new()
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "VES",
        SecondaryCurrencySymbol = "Bs.S"
    };

    private static MonetaryProfileDto ValidSingleProfile(bool isConfigured = true) => new()
    {
        Mode = MonetaryMode.Single,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = null,
        SecondaryCurrencySymbol = null,
        IsConfigured = isConfigured
    };

    private static async Task AssertSaveRejectedAsync(MonetaryProfileDto profile, string expectedMessage)
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(profile));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // Defaults / lectura.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_WhenUnconfigured_ReturnsDefaultsNotConfigured()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var profile = await service.GetAsync();

        Assert.Equal(MonetaryMode.Dual, profile.Mode);
        Assert.Equal("USD", profile.PrimaryCurrencyCode);
        Assert.Equal("$", profile.PrimaryCurrencySymbol);
        Assert.Equal("VES", profile.SecondaryCurrencyCode);
        Assert.Equal("Bs.S", profile.SecondaryCurrencySymbol);
        Assert.False(profile.IsConfigured);
    }

    [Fact]
    public async Task GetAsync_WhenModeMissingButOtherKeysExist_ReturnsDefaultsNotConfigured()
    {
        using var db = CreateInMemoryDb();
        db.SystemSettings.AddRange(
            new SystemSetting { Key = SettingKeys.Monetary.PrimaryCurrencyCode, Value = "EUR" },
            new SystemSetting { Key = SettingKeys.Monetary.PrimaryCurrencySymbol, Value = "€" },
            new SystemSetting { Key = SettingKeys.Monetary.SecondaryCurrencyCode, Value = "GBP" },
            new SystemSetting { Key = SettingKeys.Monetary.SecondaryCurrencySymbol, Value = "£" });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var profile = await service.GetAsync();

        Assert.False(profile.IsConfigured);
        Assert.Equal(MonetaryMode.Dual, profile.Mode);
        Assert.Equal("USD", profile.PrimaryCurrencyCode);
        Assert.Equal("$", profile.PrimaryCurrencySymbol);
        Assert.Equal("VES", profile.SecondaryCurrencyCode);
        Assert.Equal("Bs.S", profile.SecondaryCurrencySymbol);
    }

    [Fact]
    public async Task GetAsync_WhenStoredProfileIsCorrupted_ReturnsDefaultsNotConfigured()
    {
        using var db = CreateInMemoryDb();
        db.SystemSettings.AddRange(
            new SystemSetting { Key = SettingKeys.Monetary.Mode, Value = "Dual" },
            new SystemSetting { Key = SettingKeys.Monetary.PrimaryCurrencyCode, Value = "BAD" },
            new SystemSetting { Key = SettingKeys.Monetary.PrimaryCurrencySymbol, Value = "$" },
            new SystemSetting { Key = SettingKeys.Monetary.SecondaryCurrencyCode, Value = "VES" },
            new SystemSetting { Key = SettingKeys.Monetary.SecondaryCurrencySymbol, Value = "Bs.S" });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var profile = await service.GetAsync();

        Assert.False(profile.IsConfigured);
        Assert.Equal(MonetaryMode.Dual, profile.Mode);
        Assert.Equal("USD", profile.PrimaryCurrencyCode);
        Assert.Equal("$", profile.PrimaryCurrencySymbol);
        Assert.Equal("VES", profile.SecondaryCurrencyCode);
        Assert.Equal("Bs.S", profile.SecondaryCurrencySymbol);
    }

    [Fact]
    public async Task GetAsync_SingleWithStaleSecondaryKeys_IgnoresSecondaryAndReturnsConfigured()
    {
        using var db = CreateInMemoryDb();
        db.SystemSettings.AddRange(
            new SystemSetting { Key = SettingKeys.Monetary.Mode, Value = "Single" },
            new SystemSetting { Key = SettingKeys.Monetary.PrimaryCurrencyCode, Value = "eur " },
            new SystemSetting { Key = SettingKeys.Monetary.PrimaryCurrencySymbol, Value = " € " },
            new SystemSetting { Key = SettingKeys.Monetary.SecondaryCurrencyCode, Value = "VES" },
            new SystemSetting { Key = SettingKeys.Monetary.SecondaryCurrencySymbol, Value = "Bs.S" });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var profile = await service.GetAsync();

        Assert.True(profile.IsConfigured);
        Assert.Equal(MonetaryMode.Single, profile.Mode);
        Assert.Equal("EUR", profile.PrimaryCurrencyCode);
        Assert.Equal("€", profile.PrimaryCurrencySymbol);
        Assert.Null(profile.SecondaryCurrencyCode);
        Assert.Null(profile.SecondaryCurrencySymbol);
    }

    // ---------------------------------------------------------------------------------------------
    // Guardado válido.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_ValidDual_PersistsFiveKeysNormalizesAndRoundTrips()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var saved = await service.SaveAsync(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "eur ",
            PrimaryCurrencySymbol = " € ",
            SecondaryCurrencyCode = "gbp",
            SecondaryCurrencySymbol = " £ "
        });

        Assert.True(saved.IsConfigured);
        Assert.Equal(MonetaryMode.Dual, saved.Mode);
        Assert.Equal("EUR", saved.PrimaryCurrencyCode);
        Assert.Equal("€", saved.PrimaryCurrencySymbol);
        Assert.Equal("GBP", saved.SecondaryCurrencyCode);
        Assert.Equal("£", saved.SecondaryCurrencySymbol);

        var stored = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        Assert.Equal(5, stored.Count);
        Assert.Equal("Dual", stored[SettingKeys.Monetary.Mode]);
        Assert.Equal("EUR", stored[SettingKeys.Monetary.PrimaryCurrencyCode]);
        Assert.Equal("€", stored[SettingKeys.Monetary.PrimaryCurrencySymbol]);
        Assert.Equal("GBP", stored[SettingKeys.Monetary.SecondaryCurrencyCode]);
        Assert.Equal("£", stored[SettingKeys.Monetary.SecondaryCurrencySymbol]);

        var reloaded = await service.GetAsync();
        Assert.True(reloaded.IsConfigured);
        Assert.Equal(MonetaryMode.Dual, reloaded.Mode);
        Assert.Equal("EUR", reloaded.PrimaryCurrencyCode);
        Assert.Equal("€", reloaded.PrimaryCurrencySymbol);
        Assert.Equal("GBP", reloaded.SecondaryCurrencyCode);
        Assert.Equal("£", reloaded.SecondaryCurrencySymbol);
    }

    [Fact]
    public async Task GetAsync_ReadsTheFiveProfileKeysInOneBatch()
    {
        var settings = new Moq.Mock<ISystemSettingsService>();
        settings
            .Setup(s => s.GetSettingsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string?>
            {
                [SettingKeys.Monetary.Mode] = "Dual",
                [SettingKeys.Monetary.PrimaryCurrencyCode] = "USD",
                [SettingKeys.Monetary.PrimaryCurrencySymbol] = "$",
                [SettingKeys.Monetary.SecondaryCurrencyCode] = "VES",
                [SettingKeys.Monetary.SecondaryCurrencySymbol] = "Bs.S"
            });
        settings
            .Setup(s => s.GetSettingAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("individual reads are not allowed for a profile snapshot"));

        var service = new MonetaryProfileService(settings.Object);
        var profile = await service.GetAsync();

        Assert.True(profile.IsConfigured);
        Assert.Equal("USD", profile.PrimaryCurrencyCode);
        Assert.Equal("VES", profile.SecondaryCurrencyCode);
        settings.Verify(
            s => s.GetSettingsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        settings.Verify(s => s.GetSettingAsync(It.IsAny<string>()), Times.Never);
    }
    [Fact]
    public async Task SaveAsync_IgnoresIncomingIsConfiguredAndDerivesItFromPersistence()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);
        var profile = ValidSingleProfile(isConfigured: false);

        var saved = await service.SaveAsync(profile);

        Assert.True(saved.IsConfigured);
        var reloaded = await service.GetAsync();
        Assert.True(reloaded.IsConfigured);
    }

    [Fact]
    public async Task SaveAsync_ValidSingle_WritesOnlyModeAndPrimaryKeys()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var saved = await service.SaveAsync(ValidSingleProfile());

        Assert.True(saved.IsConfigured);
        Assert.Equal(MonetaryMode.Single, saved.Mode);
        Assert.Equal("USD", saved.PrimaryCurrencyCode);
        Assert.Equal("$", saved.PrimaryCurrencySymbol);
        Assert.Null(saved.SecondaryCurrencyCode);
        Assert.Null(saved.SecondaryCurrencySymbol);

        var storedKeys = await db.SystemSettings.AsNoTracking().Select(s => s.Key).ToListAsync();
        Assert.Equal(3, storedKeys.Count);
        Assert.Contains(SettingKeys.Monetary.Mode, storedKeys);
        Assert.Contains(SettingKeys.Monetary.PrimaryCurrencyCode, storedKeys);
        Assert.Contains(SettingKeys.Monetary.PrimaryCurrencySymbol, storedKeys);
        Assert.DoesNotContain(SettingKeys.Monetary.SecondaryCurrencyCode, storedKeys);
        Assert.DoesNotContain(SettingKeys.Monetary.SecondaryCurrencySymbol, storedKeys);

        var reloaded = await service.GetAsync();
        Assert.True(reloaded.IsConfigured);
        Assert.Equal(MonetaryMode.Single, reloaded.Mode);
        Assert.Null(reloaded.SecondaryCurrencyCode);
        Assert.Null(reloaded.SecondaryCurrencySymbol);
    }

    [Fact]
    public async Task SaveAsync_SwitchDualToSingle_PreservesPreviousSecondaryKeys()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        await service.SaveAsync(ValidDualProfile());
        await service.SaveAsync(ValidSingleProfile());

        var stored = await db.SystemSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        Assert.Equal(5, stored.Count);
        Assert.Equal("Single", stored[SettingKeys.Monetary.Mode]);
        Assert.Equal("VES", stored[SettingKeys.Monetary.SecondaryCurrencyCode]);
        Assert.Equal("Bs.S", stored[SettingKeys.Monetary.SecondaryCurrencySymbol]);
    }

    // ---------------------------------------------------------------------------------------------
    // Validación: mensajes exactos y cero persistencia.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_WithUndefinedMode_ThrowsExactMessageAndPersistsNothing()
    {
        var profile = ValidDualProfile();
        profile.Mode = (MonetaryMode)99;

        await AssertSaveRejectedAsync(profile, UndefinedModeMessage);
    }

    [Fact]
    public async Task SaveAsync_WithMalformedPrimaryCode_ThrowsExactMessageAndPersistsNothing()
    {
        var profile = ValidDualProfile();
        profile.PrimaryCurrencyCode = "US";

        await AssertSaveRejectedAsync(profile, MalformedPrimaryCodeMessage);
    }

    [Fact]
    public async Task SaveAsync_WithUnsupportedPrimaryCode_ThrowsExactMessageAndPersistsNothing()
    {
        var profile = ValidDualProfile();
        profile.PrimaryCurrencyCode = "AAA";

        await AssertSaveRejectedAsync(profile, string.Format(UnsupportedCodeMessage, "AAA"));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("123456789")]
    public async Task SaveAsync_WithInvalidPrimarySymbol_ThrowsExactMessageAndPersistsNothing(string symbol)
    {
        var profile = ValidDualProfile();
        profile.PrimaryCurrencySymbol = symbol;

        await AssertSaveRejectedAsync(profile, PrimarySymbolMessage);
    }

    [Fact]
    public async Task SaveAsync_WithControlCharacterInPrimarySymbol_ThrowsExactMessageAndPersistsNothing()
    {
        var profile = ValidDualProfile();
        profile.PrimaryCurrencySymbol = "$\u0001";

        await AssertSaveRejectedAsync(profile, ControlCharacterMessage);
    }
    [Theory]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public async Task SaveAsync_WithUnicodeLineSeparatorInSymbol_ThrowsExactMessage(string separator)
    {
        var profile = ValidDualProfile();
        profile.PrimaryCurrencySymbol = "$" + separator + "X";

        await AssertSaveRejectedAsync(profile, ControlCharacterMessage);
    }

    [Fact]
    public async Task SaveAsync_SingleWithSecondaryCode_ThrowsExactMessageAndPersistsNothing()
    {
        var profile = ValidSingleProfile();
        profile.SecondaryCurrencyCode = "VES";

        await AssertSaveRejectedAsync(profile, SingleWithSecondaryMessage);
    }

    [Fact]
    public async Task SaveAsync_SingleWithSecondarySymbol_ThrowsExactMessageAndPersistsNothing()
    {
        var profile = ValidSingleProfile();
        profile.SecondaryCurrencySymbol = "Bs.S";

        await AssertSaveRejectedAsync(profile, SingleWithSecondaryMessage);
    }

    [Fact]
    public async Task SaveAsync_DualWithoutSecondaryCode_ThrowsExactMessageAndPersistsNothing()
    {
        var profile = ValidDualProfile();
        profile.SecondaryCurrencyCode = null;

        await AssertSaveRejectedAsync(profile, DualWithoutSecondaryMessage);
    }

    [Fact]
    public async Task SaveAsync_WithMalformedSecondaryCode_ThrowsExactMessageAndPersistsNothing()
    {
        var profile = ValidDualProfile();
        profile.SecondaryCurrencyCode = "VE";

        await AssertSaveRejectedAsync(profile, MalformedSecondaryCodeMessage);
    }

    [Fact]
    public async Task SaveAsync_WithUnsupportedSecondaryCode_ThrowsExactMessageAndPersistsNothing()
    {
        var profile = ValidDualProfile();
        profile.SecondaryCurrencyCode = "AAA";

        await AssertSaveRejectedAsync(profile, string.Format(UnsupportedCodeMessage, "AAA"));
    }

    [Fact]
    public async Task SaveAsync_WithSecondaryEqualToPrimary_ThrowsExactMessageAndPersistsNothing()
    {
        var profile = ValidDualProfile();
        profile.SecondaryCurrencyCode = "usd";

        await AssertSaveRejectedAsync(profile, SameCurrencyMessage);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("123456789")]
    public async Task SaveAsync_WithInvalidSecondarySymbol_ThrowsExactMessageAndPersistsNothing(string symbol)
    {
        var profile = ValidDualProfile();
        profile.SecondaryCurrencySymbol = symbol;

        await AssertSaveRejectedAsync(profile, SecondarySymbolMessage);
    }
}






