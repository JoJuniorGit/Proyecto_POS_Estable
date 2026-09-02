using System;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests;

/// <summary>
/// Pruebas unitarias de validación matemática de códigos de barras (GS1 Mod10, balanzas 20-29, SKUs)
/// y del sintetizador de tonos PCM ScannerFeedbackService.
/// </summary>
public class BarcodeScannerDecodeTests
{
    [Theory]
    // Valid EAN-13
    [InlineData("7591001002009", true)]
    [InlineData("5901234123457", true)]
    // Valid UPC-A
    [InlineData("036000291452", true)]
    // Valid EAN-8
    [InlineData("75910013", true)]
    // Valid GS1 In-store scale barcode (prefix 20-29)
    [InlineData("2012345005006", true)]
    // Valid alphanumeric and internal SKUs
    [InlineData("SKU123456", true)]
    [InlineData("PROD001", true)]
    [InlineData("1001", true)]
    [InlineData("123456789012345", true)] // Exactly 15 chars
    // Corrupted GS1 barcodes with wrong check digit
    [InlineData("7591001002003", false)] // Bad EAN-13 check digit
    [InlineData("5901234123450", false)] // Bad EAN-13 check digit
    [InlineData("036000291459", false)] // Bad UPC-A check digit
    [InlineData("75910019", false)] // Bad EAN-8 check digit
    [InlineData("2012345005009", false)] // Bad scale check digit
    // URLs / QR
    [InlineData("http://example.com/item?id=10", false)]
    [InlineData("https://menu.pos.com/qr", false)]
    [InlineData("www.example.com?query=1&param=2", false)]
    [InlineData("PROD 123", false)] // Space
    [InlineData("SKU-1001", false)] // Hyphen/Symbol
    [InlineData("123", false)] // Too short (< 4 chars)
    [InlineData("1234567890123456", false)] // Too long (> 15 chars)
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void BarcodeValidator_ValidatesFormatsProperly(string? code, bool expectedValid)
    {
        bool isValid = Core.Helpers.BarcodeValidator.IsValidBarcode(code);
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public void ScannerFeedbackService_PlaysTonesWithoutThrowing()
    {
        using var feedback = new ScannerFeedbackService();
        var exSuccess = Record.Exception(() => feedback.PlaySuccess());
        var exNotFound = Record.Exception(() => feedback.PlayNotFound());
        var exError = Record.Exception(() => feedback.PlayError());

        Assert.Null(exSuccess);
        Assert.Null(exNotFound);
        Assert.Null(exError);
    }
}
