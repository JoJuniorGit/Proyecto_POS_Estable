using System.Globalization;
using Core.Helpers;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.143: el ajuste de formato de moneda (Venezolano/Internacional) debe ser determinista
// e independiente de la cultura del SO donde corra el proceso.
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
}
