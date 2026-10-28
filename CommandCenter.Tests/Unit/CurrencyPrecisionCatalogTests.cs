using Core.Common;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T2b (S5): ISO 4217 minor units per currency plus the historical VES integer-cash policy.
public class CurrencyPrecisionCatalogTests
{
    [Theory]
    [InlineData("USD", 2, 2)]
    [InlineData("EUR", 2, 2)]
    [InlineData("VES", 2, 0)]
    [InlineData("Bs.S", 2, 0)]
    [InlineData("BsS", 2, 0)]
    [InlineData("JPY", 0, 0)]
    [InlineData("KWD", 3, 3)]
    [InlineData("CLP", 0, 0)]
    [InlineData("CLF", 4, 4)]
    [InlineData("UYW", 4, 4)]
    [InlineData("AUD", 2, 2)]
    public void For_KnownCurrency_ReturnsIsoMinorUnitsAndCashPolicy(
        string code,
        int expectedDigital,
        int expectedCash)
    {
        var precision = CurrencyPrecisionCatalog.For(code);

        Assert.Equal(expectedDigital, precision.DigitalDecimals);
        Assert.Equal(expectedCash, precision.CashDecimals);
    }

    [Fact]
    public void For_LowercaseCode_NormalizesBeforeResolving()
    {
        var precision = CurrencyPrecisionCatalog.For("usd");

        Assert.Equal(new CurrencyPrecision(2, 2), precision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void For_MissingCode_ThrowsObligatoryMessage(string? code)
    {
        var exception = Assert.Throws<ArgumentException>(() => CurrencyPrecisionCatalog.For(code!));

        Assert.Equal("El código de moneda es obligatorio.", exception.Message);
    }

    [Theory]
    [InlineData("US", "El código 'US' no es un código ISO 4217 reconocido.")]
    [InlineData("U1D", "El código 'U1D' no es un código ISO 4217 reconocido.")]
    [InlineData("ZZZ", "El código 'ZZZ' no es un código ISO 4217 reconocido.")]
    public void For_MalformedOrUnsupportedCode_ThrowsExactMessage(string code, string expectedMessage)
    {
        var exception = Assert.Throws<ArgumentException>(() => CurrencyPrecisionCatalog.For(code));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Theory]
    [InlineData("JPY")]
    [InlineData("KWD")]
    [InlineData("CLP")]
    [InlineData("CLF")]
    [InlineData("UYW")]
    [InlineData("VES")]
    public void Iso4217_SupportsCodesRequiredByThePrecisionCatalog(string code)
    {
        Assert.True(Iso4217.IsSupported(Iso4217.Normalize(code)));
    }
}
