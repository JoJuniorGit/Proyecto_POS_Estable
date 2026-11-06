using Sales.Module.Receipts;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T6-U14: PDF text keeps WinAnsi accents/punctuation instead of collapsing to '?'.
public class PdfTextEscapeTests
{
    [Theory]
    [InlineData("á", "\\341")]
    [InlineData("€", "\\200")]
    [InlineData("“", "\\223")]
    [InlineData("—", "\\227")]
    [InlineData("ñ", "\\361")]
    public void EncodeChar_MapsWinAnsiCharactersToOctal(string value, string expected)
    {
        Assert.Equal(expected, PdfTextEscape.Escape(value));
    }

    [Fact]
    public void Escape_PreservesAsciiAndEscapesPdfDelimiters()
    {
        Assert.Equal(@"Caf\351 \(sin leche\)", PdfTextEscape.Escape("Café (sin leche)"));
    }

    [Fact]
    public void Escape_UnrepresentableCharacterFallsBackToQuestionMark()
    {
        Assert.Equal("?", PdfTextEscape.Escape("₡"));
    }
}

