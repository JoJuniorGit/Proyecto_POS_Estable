using System.Globalization;
using System.Windows;
using Core.DTOs;
using Core.Helpers;
using Desktop.Client.Converters;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T4c2b1 (S9): los convertidores WPF de CheckoutDialog resuelven símbolos, montos y
// visibilidad desde el perfil activo de CurrencyDisplay. El formato numérico sigue delegado
// en CurrencyDisplay.Number y no se duplica aquí.
public class CurrencyDisplayConvertersTests
{
    [Fact]
    public void DualProfile_UsdVes_FormatsPrimaryAndSecondaryAndKeepsDualLineVisible()
    {
        var previousFormat = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "VES",
                SecondaryCurrencySymbol = "Bs.S",
                IsConfigured = true
            });

            Assert.Equal("$ 1.250,50", Convert(new PrimaryAmountConverter(), 1250.50m));
            Assert.Equal("Bs.S 3.500,25", Convert(new LocalAmountConverter(), 3500.25m));
            Assert.Equal("$", Convert(new PrimaryCurrencySymbolConverter(), true));
            Assert.Equal("Bs.S", Convert(new LocalCurrencySymbolConverter(), true));
            Assert.Equal("USD", Convert(new PrimaryCurrencyCodeConverter(), true));
            Assert.Equal("VES", Convert(new LocalCurrencyCodeConverter(), true));
            Assert.Equal(Visibility.Visible, Convert(new DualOnlyVisibilityConverter(), true));
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void SingleProfile_Eur_UsesPrimaryForBothAmountsAndCollapsesDualLine()
    {
        var previousFormat = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "EUR",
                PrimaryCurrencySymbol = "€",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null,
                IsConfigured = true
            });

            // En Single no existe moneda secundaria: el formato local colapsa a la primaria.
            Assert.Equal("€ 1.250,50", Convert(new PrimaryAmountConverter(), 1250.50m));
            Assert.Equal("€ 1.250,50", Convert(new LocalAmountConverter(), 1250.50m));
            Assert.Equal("€", Convert(new LocalCurrencySymbolConverter(), true));
            Assert.Equal("EUR", Convert(new LocalCurrencyCodeConverter(), true));
            Assert.Null(CurrencyDisplay.SecondaryCurrencyCode);
            Assert.Equal(Visibility.Collapsed, Convert(new DualOnlyVisibilityConverter(), true));
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void AmountConverters_NullValue_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Convert(new LocalAmountConverter(), null));
        Assert.Equal(string.Empty, Convert(new PrimaryAmountConverter(), null));
    }

    [Fact]
    public void AmountConverters_DefensiveNumericTypes_FormatWithoutThrowing()
    {
        var previousFormat = CurrencyDisplay.Current;
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            CurrencyDisplay.SetMonetaryProfile(null);

            Assert.Equal("Bs.S 42,00", Convert(new LocalAmountConverter(), 42));
            Assert.Equal("Bs.S 10,50", Convert(new LocalAmountConverter(), 10.5d));
            Assert.Equal("$ 7,00", Convert(new PrimaryAmountConverter(), 7L));
            Assert.Equal(string.Empty, Convert(new LocalAmountConverter(), "not-a-number"));
        }
        finally
        {
            CurrencyDisplay.Current = previousFormat;
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    private static object Convert(System.Windows.Data.IValueConverter converter, object? value) =>
        converter.Convert(value!, typeof(string), null!, CultureInfo.InvariantCulture);
}
