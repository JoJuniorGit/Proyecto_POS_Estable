using Core.Common;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T3c1 (F3c1): legacy local-currency aliases resolve to the canonical ISO code VES.
public class CurrencyCodeNormalizerTests
{
    [Theory]
    [InlineData("Bs.S", "VES")]
    [InlineData("BsS", "VES")]
    [InlineData("VES", "VES")]
    [InlineData("ves", "VES")]
    [InlineData("Bs.S ", "VES")]
    [InlineData("bs.s", "VES")]
    [InlineData("USD", "USD")]
    [InlineData("usd", "USD")]
    [InlineData("EUR", "EUR")]
    public void NormalizeToIso_KnownCode_ReturnsCanonicalIso(string code, string expected)
    {
        Assert.Equal(expected, CurrencyCodeNormalizer.NormalizeToIso(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("US")]
    [InlineData("ZZZ")]
    public void NormalizeToIso_InvalidCode_ReturnsNull(string? code)
    {
        Assert.Null(CurrencyCodeNormalizer.NormalizeToIso(code));
    }

    [Theory]
    [InlineData("Bs.S", true)]
    [InlineData("BsS", true)]
    [InlineData("VES", true)]
    [InlineData("ves", true)]
    [InlineData("USD", false)]
    [InlineData("EUR", false)]
    [InlineData("ZZZ", false)]
    [InlineData(null, false)]
    public void IsLocal_ResolvesCanonicalCode(string? code, bool expected)
    {
        Assert.Equal(expected, CurrencyCodeNormalizer.IsLocal(code));
    }
}
