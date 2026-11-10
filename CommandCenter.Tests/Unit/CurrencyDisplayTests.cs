using System;
using System.Globalization;
using Core.DTOs;
using Core.Helpers;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.143: el ajuste de formato de moneda (Venezolano/Internacional) debe ser determinista
// e independiente de la cultura del SO donde corra el proceso.
// 8.211-T4c1 (S9): el estado del perfil monetario (modo/códigos/símbolos) vive en la misma
// clase y debe degradar a los defaults Dual USD/VES sin romper el formato numérico existente.
public class CurrencyDisplayTests
{
    [Theory]
    [InlineData("International", MoneyDisplayFormat.International)]
    [InlineData("international", MoneyDisplayFormat.International)]
    [InlineData("INTERNATIONAL", MoneyDisplayFormat.International)]
    [InlineData("Venezuelan", MoneyDisplayFormat.Venezuelan)]
    [InlineData("venezuelan", MoneyDisplayFormat.Venezuelan)]
    [InlineData("Euro", MoneyDisplayFormat.Venezuelan)]
    [InlineData("", MoneyDisplayFormat.Venezuelan)]
    [InlineData("   ", MoneyDisplayFormat.Venezuelan)]
    [InlineData(null, MoneyDisplayFormat.Venezuelan)]
    public void ParseFormat_NormalizesSettingValue(string? value, MoneyDisplayFormat expected)
    {
        Assert.Equal(expected, MoneyFormat.ParseFormat(value));
    }

    [Fact]
    public void Number_Venezuelan_UsesEsVeSeparators()
    {
        Assert.Equal("1.250,50", MoneyFormat.Number(1250.50m, MoneyDisplayFormat.Venezuelan));
        Assert.Equal("73,0000", MoneyFormat.Number(73m, MoneyDisplayFormat.Venezuelan, 4));
    }

    [Fact]
    public void Number_International_UsesInvariantSeparators()
    {
        Assert.Equal("1,250.50", MoneyFormat.Number(1250.50m, MoneyDisplayFormat.International));
        Assert.Equal("73.0000", MoneyFormat.Number(73m, MoneyDisplayFormat.International, 4));
    }

    [Fact]
    public void Number_DoesNotDependOnAmbientCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

            Assert.Equal("1.250,50", MoneyFormat.Number(1250.50m, MoneyDisplayFormat.Venezuelan));
            Assert.Equal("1,250.50", MoneyFormat.Number(1250.50m, MoneyDisplayFormat.International));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void CurrencyDisplay_SetFromSetting_UpdatesCurrentAndNumberFormat()
    {
        var previous = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.SetFromSetting("International");
            Assert.Equal(MoneyDisplayFormat.International, CurrencyDisplay.Current);
            Assert.Equal("1,250.50", CurrencyDisplay.Number(1250.50m));

            CurrencyDisplay.SetFromSetting("Venezuelan");
            Assert.Equal(MoneyDisplayFormat.Venezuelan, CurrencyDisplay.Current);
            Assert.Equal("1.250,50", CurrencyDisplay.Number(1250.50m));

            CurrencyDisplay.SetFromSetting(null);
            Assert.Equal(MoneyDisplayFormat.Venezuelan, CurrencyDisplay.Current);
            Assert.Equal("1.250,50", CurrencyDisplay.Number(1250.50m));
        }
        finally
        {
            CurrencyDisplay.Current = previous;
        }
    }

    [Fact]
    public void CurrencyDisplay_DefaultCurrent_IsVenezuelan()
    {
        // La suite está serializada y toda prueba que muta Current lo restaura en finally.
        Assert.Equal(MoneyDisplayFormat.Venezuelan, CurrencyDisplay.Current);
    }

    [Fact]
    public void MonetaryProfile_NullDefaultsToDualUsdVes()
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(null);

            Assert.Equal(MonetaryMode.Dual, CurrencyDisplay.Mode);
            Assert.False(CurrencyDisplay.IsSingle);
            Assert.Equal("USD", CurrencyDisplay.PrimaryCurrencyCode);
            Assert.Equal("$", CurrencyDisplay.PrimaryCurrencySymbol);
            Assert.Equal("VES", CurrencyDisplay.SecondaryCurrencyCode);
            Assert.Equal("Bs.S", CurrencyDisplay.SecondaryCurrencySymbol);
            Assert.Equal("VES", CurrencyDisplay.LocalCurrencyCode);
            Assert.Equal("Bs.S", CurrencyDisplay.LocalCurrencySymbol);
        });
    }

    [Fact]
    public void MonetaryProfile_DualCop_UsesConfiguredSymbolsAndFormatsBothCurrencies()
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "COP",
                SecondaryCurrencySymbol = "COL$",
                IsConfigured = true
            });

            Assert.False(CurrencyDisplay.IsSingle);
            Assert.Equal("COP", CurrencyDisplay.SecondaryCurrencyCode);
            Assert.Equal("COL$", CurrencyDisplay.SecondaryCurrencySymbol);
            Assert.Equal("COP", CurrencyDisplay.LocalCurrencyCode);
            Assert.Equal("COL$", CurrencyDisplay.LocalCurrencySymbol);
            Assert.Equal("$ 1.250,50", CurrencyDisplay.FormatPrimary(1250.50m));
            Assert.Equal("COL$ 3.500,25", CurrencyDisplay.FormatSecondary(3500.25m));
        });
    }

    [Fact]
    public void MonetaryProfile_SingleCop_ClearsSecondaryAndUsesPrimaryAsLocal()
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "COP",
                PrimaryCurrencySymbol = "COL$",
                SecondaryCurrencyCode = "VES",
                SecondaryCurrencySymbol = "Bs.S"
            });

            Assert.True(CurrencyDisplay.IsSingle);
            Assert.Null(CurrencyDisplay.SecondaryCurrencyCode);
            Assert.Null(CurrencyDisplay.SecondaryCurrencySymbol);
            Assert.Equal("COP", CurrencyDisplay.LocalCurrencyCode);
            Assert.Equal("COL$", CurrencyDisplay.LocalCurrencySymbol);
            Assert.Equal("COL$ 900,00", CurrencyDisplay.FormatPrimary(900m));
            // En Single no existe una segunda moneda: el formato secundario cae a la primaria.
            Assert.Equal("COL$ 900,00", CurrencyDisplay.FormatSecondary(900m));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MonetaryProfile_VesPresentation_UsesBsSWhenSymbolMissing(string? secondarySymbol)
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "bs.s",
                SecondaryCurrencySymbol = secondarySymbol
            });

            Assert.Equal("VES", CurrencyDisplay.SecondaryCurrencyCode);
            Assert.Equal("Bs.S", CurrencyDisplay.SecondaryCurrencySymbol);
            Assert.Equal("Bs.S", CurrencyDisplay.LocalCurrencySymbol);
            Assert.Equal("Bs.S", CurrencyDisplay.SymbolFor("VES"));
            Assert.Equal("Bs.S", CurrencyDisplay.SymbolFor("Bs.S"));
            Assert.Equal("Bs.S", CurrencyDisplay.SymbolFor("BsS"));
            Assert.Equal("Bs.S 100,00", CurrencyDisplay.FormatSecondary(100m));
        });
    }

    [Fact]
    public void MonetaryProfile_SingleVes_UsesBsSPresentation()
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "VES",
                PrimaryCurrencySymbol = null!
            });

            Assert.True(CurrencyDisplay.IsSingle);
            Assert.Equal("VES", CurrencyDisplay.PrimaryCurrencyCode);
            Assert.Equal("Bs.S", CurrencyDisplay.PrimaryCurrencySymbol);
            Assert.Equal("VES", CurrencyDisplay.LocalCurrencyCode);
            Assert.Equal("Bs.S", CurrencyDisplay.LocalCurrencySymbol);
            Assert.Equal("Bs.S 1.234,56", CurrencyDisplay.FormatPrimary(1234.56m));
        });
    }

    [Fact]
    public void MonetaryProfile_UsesConfiguredUnicodeSymbol_WithoutQuestionMark()
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "EUR",
                PrimaryCurrencySymbol = "€",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null
            });

            Assert.Equal("€", CurrencyDisplay.PrimaryCurrencySymbol);
            Assert.Equal("€", CurrencyDisplay.SymbolFor("EUR"));
            Assert.Equal("€ 1.250,50", CurrencyDisplay.FormatPrimary(1250.50m));
            Assert.DoesNotContain("?", CurrencyDisplay.FormatPrimary(1250.50m));
        });
    }

    [Fact]
    public void MonetaryProfile_MalformedFieldsFallBackSafely()
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "not-iso",
                PrimaryCurrencySymbol = "toolongsymbol",
                SecondaryCurrencyCode = "COP",
                SecondaryCurrencySymbol = "COL$"
            });

            // La primaria inválida cae a USD/$; la secundaria válida se conserva.
            Assert.Equal("USD", CurrencyDisplay.PrimaryCurrencyCode);
            Assert.Equal("$", CurrencyDisplay.PrimaryCurrencySymbol);
            Assert.Equal("COP", CurrencyDisplay.SecondaryCurrencyCode);
            Assert.Equal("COL$", CurrencyDisplay.SecondaryCurrencySymbol);
        });
    }

    [Fact]
    public void MonetaryProfile_UndefinedModeFallsBackToDefaults()
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = (MonetaryMode)99,
                PrimaryCurrencyCode = "COP",
                PrimaryCurrencySymbol = "COL$",
                SecondaryCurrencyCode = "EUR",
                SecondaryCurrencySymbol = "€"
            });

            Assert.Equal(MonetaryMode.Dual, CurrencyDisplay.Mode);
            Assert.Equal("USD", CurrencyDisplay.PrimaryCurrencyCode);
            Assert.Equal("$", CurrencyDisplay.PrimaryCurrencySymbol);
            Assert.Equal("VES", CurrencyDisplay.SecondaryCurrencyCode);
            Assert.Equal("Bs.S", CurrencyDisplay.SecondaryCurrencySymbol);
        });
    }

    [Fact]
    public void MonetaryProfile_DualRejectsSecondaryEqualToPrimary()
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "usd",
                SecondaryCurrencySymbol = "US$"
            });

            Assert.Equal("VES", CurrencyDisplay.SecondaryCurrencyCode);
            Assert.Equal("Bs.S", CurrencyDisplay.SecondaryCurrencySymbol);
            Assert.NotEqual(CurrencyDisplay.PrimaryCurrencyCode, CurrencyDisplay.SecondaryCurrencyCode);
        });
    }

    [Fact]
    public void SymbolFor_ReturnsEmptyForInvalidCodesAndIsoForUnknownCurrencies()
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(null);

            Assert.Equal(string.Empty, CurrencyDisplay.SymbolFor(null));
            Assert.Equal(string.Empty, CurrencyDisplay.SymbolFor(""));
            Assert.Equal(string.Empty, CurrencyDisplay.SymbolFor("   "));
            Assert.Equal(string.Empty, CurrencyDisplay.SymbolFor("X"));
            Assert.Equal(string.Empty, CurrencyDisplay.SymbolFor("??"));
            Assert.Equal("$", CurrencyDisplay.SymbolFor("usd"));
            Assert.Equal("Bs.S", CurrencyDisplay.SymbolFor("bs.s"));
            // Una moneda ISO ajena al perfil se presenta con su propio código, nunca con '?'.
            Assert.Equal("EUR", CurrencyDisplay.SymbolFor("EUR"));
        });
    }

    [Fact]
    public void MonetaryProfile_FormattingDelegatesToActiveCurrencyFormat()
    {
        RunWithMonetaryProfile(() =>
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "VES",
                SecondaryCurrencySymbol = "Bs.S"
            });

            CurrencyDisplay.Current = MoneyDisplayFormat.International;
            Assert.Equal("$ 1,250.50", CurrencyDisplay.FormatPrimary(1250.50m));
            Assert.Equal("Bs.S 3,500.25", CurrencyDisplay.FormatSecondary(3500.25m));
            Assert.Equal("$ 73.0000", CurrencyDisplay.FormatPrimary(73m, 4));

            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            Assert.Equal("$ 1.250,50", CurrencyDisplay.FormatPrimary(1250.50m));
            Assert.Equal("Bs.S 3.500,25", CurrencyDisplay.FormatSecondary(3500.25m));
        });
    }

    private static void RunWithMonetaryProfile(Action action)
    {
        var previousFormat = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            action();
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            // La suite está serializada: se restaura el perfil default para no contaminar
            // otras pruebas que dependen de CurrencyDisplay.
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }
}
