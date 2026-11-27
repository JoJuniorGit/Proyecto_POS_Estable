using Desktop.Client.Validation;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class QuantityInputParserTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("@#$")]
    [InlineData("1 2")]
    [InlineData("1..2")]
    [InlineData("1.2.3")]
    [InlineData("1,2,3")]
    public void TryParse_WhenTextContainsInvalidCharactersOrMultipleSeparators_ReturnsFalse(string text)
    {
        Assert.False(QuantityInputParser.TryParse(text, true, 3, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("005")]
    [InlineData("0.5")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("1,25")]
    public void TryParse_WhenDecimalProductTextIsValid_ReturnsNormalizedValue(string text)
    {
        Assert.True(QuantityInputParser.TryParse(text, true, 3, out var value, out var error));
        Assert.Null(error);
        Assert.True(value >= 0m);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1,5")]
    public void TryParse_WhenWholeProductReceivesSeparator_ReturnsFalse(string text)
    {
        Assert.False(QuantityInputParser.TryParse(text, false, 3, out _, out var error));
        Assert.Equal("Este producto solo admite cantidades enteras.", error);
    }

    [Fact]
    public void TryParse_WhenDecimalPlacesExceedMaximum_ReturnsFalse()
    {
        Assert.True(QuantityInputParser.TryParse("1.234", true, 3, out var accepted, out _));
        Assert.Equal(1.234m, accepted);

        Assert.False(QuantityInputParser.TryParse("1.2345", true, 3, out _, out var error));
        Assert.Equal("Máximo 3 decimales.", error);
    }

    [Fact]
    public void TryParse_WhenEmpty_ReturnsZeroWithoutError()
    {
        Assert.True(QuantityInputParser.TryParse("", true, 3, out var empty, out var emptyError));
        Assert.Equal(0m, empty);
        Assert.Null(emptyError);

        Assert.True(QuantityInputParser.TryParse("   ", true, 3, out var whitespace, out var whitespaceError));
        Assert.Equal(0m, whitespace);
        Assert.Null(whitespaceError);
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("1.2.3", true)]
    [InlineData("-5", true)]
    [InlineData("1,5", false)]
    public void IsValidPaste_RejectsInvalidClipboardText(string text, bool allowDecimals)
    {
        Assert.False(QuantityInputParser.IsValidPaste(text, allowDecimals, 3));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1,5")]
    [InlineData(".5")]
    public void IsValidPaste_AcceptsValidClipboardText(string text)
    {
        Assert.True(QuantityInputParser.IsValidPaste(text, true, 3));
    }

    [Fact]
    public void BuildText_ReplacesTheCurrentSelection()
    {
        var result = QuantityInputParser.BuildText("100", 0, 3, "5");

        Assert.Equal("5", result);
    }

    [Fact]
    public void Format_UsesTheActiveCultureAndDropsInsignificantZeros()
    {
        Assert.Equal("5", QuantityInputParser.Format(5m));
        Assert.Equal("0.5", QuantityInputParser.Format(0.5m).Replace(',', '.'));
    }
}
