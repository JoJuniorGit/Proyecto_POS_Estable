using Core.DTOs;
using Core.Interfaces;
using Inventory.Module.Services.Ocr;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.147-T3/D6/D7/D8: pruebas deterministas del parser heurístico de word-boxes (sin motor
/// nativo). Cubren clustering fila/columna, prioridad de la plantilla del proveedor, fallback
/// genérico, parseo numérico tolerante espejo de la ruta tabular y confianza por campo.
/// </summary>
public class OcrTableParserTests
{
    private const double DefaultHeight = 10;
    private readonly InvoiceTableParser _parser = new();

    private static OcrWord Word(
        string text,
        double x,
        double y,
        double confidence = 90,
        double width = 40,
        double height = DefaultHeight)
        => new(text, x, y, width, height, confidence);

    private static OcrPage Page(params OcrWord[] words) => new(words);

    private static OcrWord[] GenericHeader(double y = 100) =>
    [
        Word("Descripción", 40, y),
        Word("Cantidad", 200, y),
        Word("Precio", 340, y)
    ];

    private static OcrWord[] DataRow(string name, string quantity, string cost, double y) =>
    [
        Word(name, 40, y),
        Word(quantity, 200, y),
        Word(cost, 340, y)
    ];

    [Fact]
    public void TemplateKeywords_TakePriority_OverGenericForSameWord()
    {
        // La plantilla llama "Precio" a la columna de cantidad (palabra que también es keyword
        // genérica de costo unitario): la plantilla debe ganar el anclaje.
        var template = new SupplierColumnMappingDto(null, null, "Descripcion", "Precio", "Valor");
        var page = Page(
            Word("Descripcion", 40, 100),
            Word("Precio", 200, 100),
            Word("Valor", 340, 100),
            Word("Harina", 40, 130),
            Word("2", 200, 130),
            Word("4,50", 340, 130));

        var line = Assert.Single(_parser.Parse([page], template));
        Assert.Equal("Harina", line.Name);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4.50m, line.UnitCost);

        // Sin plantilla, el mismo encabezado cae en el fallback genérico con otro mapeo: "Precio"
        // pasa a ser costo unitario y la banda numérica une "2" + "4,50".
        var fallback = Assert.Single(_parser.Parse([page], template: null));
        Assert.Null(fallback.Quantity);
        Assert.Equal(24.50m, fallback.UnitCost);
    }

    [Fact]
    public void TemplateHeaderRow_WinsTieAgainstGenericHeaderRow()
    {
        // Dos filas candidatas con 2 columnas cada una; gana la que matchea nombres de plantilla.
        var template = new SupplierColumnMappingDto(null, null, "Concepto", "Bultos", "Valor");
        var page = Page(
            Word("Descripcion", 40, 100),
            Word("Cantidad", 200, 100),
            Word("Concepto", 40, 130),
            Word("Bultos", 340, 130),
            Word("Harina", 40, 160),
            Word("3", 340, 160));

        var line = Assert.Single(_parser.Parse([page], template));
        Assert.Equal("Harina", line.Name);
        Assert.Equal(3m, line.Quantity);
    }

    [Fact]
    public void HeaderRow_WithMostMatchedColumns_Wins()
    {
        var page = Page(
            Word("Cantidad", 200, 100),
            Word("Cantidad", 200, 130),
            Word("Precio", 340, 130),
            Word("Descripcion", 40, 160),
            Word("Cantidad", 200, 160),
            Word("Precio", 340, 160),
            Word("Café", 40, 190),
            Word("2", 200, 190),
            Word("4,50", 340, 190));

        var line = Assert.Single(_parser.Parse([page], template: null));
        Assert.Equal("Café", line.Name);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4.50m, line.UnitCost);
    }

    [Fact]
    public void GenericKeywords_ResolveColumns_WithoutTemplate()
    {
        var page = Page([.. GenericHeader(), .. DataRow("Café", "2", "$ 4,50", 130)]);

        var line = Assert.Single(_parser.Parse([page], template: null));
        Assert.Equal("Café", line.Name);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4.50m, line.UnitCost);
    }

    [Fact]
    public void GenericHeader_IsAccentAndCaseInsensitive()
    {
        var page = Page(
            Word("DESCRIPCIÓN", 40, 100),
            Word("CANTIDAD", 200, 100),
            Word("PRECIO", 340, 100),
            Word("Café", 40, 130),
            Word("2", 200, 130),
            Word("4,50", 340, 130));

        var line = Assert.Single(_parser.Parse([page], template: null));
        Assert.Equal("Café", line.Name);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4.50m, line.UnitCost);
    }

    [Fact]
    public void TemplateHeader_MatchesAccentsCaseAndMultiWordNames()
    {
        // "Valor Neto" (dos palabras, acentos distintos) no es keyword genérica: solo se ancla si
        // la plantilla matchea la secuencia completa de palabras del encabezado.
        var template = new SupplierColumnMappingDto(null, null, "Descripción", "Cantidad", "Valor Neto");
        var page = Page(
            Word("DESCRIPCION", 40, 100),
            Word("cantidad", 200, 100),
            Word("VALOR", 330, 100),
            Word("NETO", 380, 100),
            Word("Café", 40, 130),
            Word("2", 200, 130),
            Word("4,50", 330, 130));

        var line = Assert.Single(_parser.Parse([page], template));
        Assert.Equal("Café", line.Name);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4.50m, line.UnitCost);
    }

    [Fact]
    public void TolerantNumericFormats_ParseLikeFilePath()
    {
        var page = Page([
            .. GenericHeader(),
            .. DataRow("Uno", "1.234,56", "365.00", 130),
            .. DataRow("Dos", "1,234.56", "12,5", 160),
            .. DataRow("Tres", "12,5", "$ 4.250", 190),
            .. DataRow("Cuatro", "365.00", "1.234,56", 220),
            Word("Cinco", 40, 250),
            Word("$", 200, 250),
            Word("4.250", 230, 250),
            Word("1,234.56", 340, 250)]);

        var lines = _parser.Parse([page], template: null);

        Assert.Equal(5, lines.Count);
        Assert.Equal(1234.56m, lines[0].Quantity);
        Assert.Equal(365m, lines[0].UnitCost);
        Assert.Equal(1234.56m, lines[1].Quantity);
        Assert.Equal(12.5m, lines[1].UnitCost);
        Assert.Equal(12.5m, lines[2].Quantity);
        // Espejo de TryParseDecimal: punto único = separador decimal (InvariantCulture).
        Assert.Equal(4.25m, lines[2].UnitCost);
        Assert.Equal(365m, lines[3].Quantity);
        Assert.Equal(1234.56m, lines[3].UnitCost);
        // Banda numérica partida en dos palabras: "$" + "4.250" se unen antes de parsear.
        Assert.Equal(4.25m, lines[4].Quantity);
        Assert.Equal(1234.56m, lines[4].UnitCost);
    }

    [Fact]
    public void ShuffledWords_StillClusterIntoRowsAndColumns()
    {
        var page = Page(
            Word("2,25", 340, 160),
            Word("Cantidad", 200, 100),
            Word("Café", 40, 130),
            Word("Azúcar", 40, 162),
            Word("Descripción", 40, 100),
            Word("2", 200, 130),
            Word("10", 200, 161),
            Word("4,50", 340, 130),
            Word("Precio", 340, 100));

        var lines = _parser.Parse([page], template: null);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Café", lines[0].Name);
        Assert.Equal(2m, lines[0].Quantity);
        Assert.Equal(4.50m, lines[0].UnitCost);
        Assert.Equal("Azúcar", lines[1].Name);
        Assert.Equal(10m, lines[1].Quantity);
        Assert.Equal(2.25m, lines[1].UnitCost);
    }

    [Fact]
    public void MissingNumericFields_YieldNullValuesAndZeroConfidence()
    {
        var page = Page([
            .. GenericHeader(),
            Word("Harina", 40, 130, confidence: 77.5)]);

        var line = Assert.Single(_parser.Parse([page], template: null));
        Assert.Equal("Harina", line.Name);
        Assert.Null(line.Quantity);
        Assert.Null(line.UnitCost);
        Assert.Equal(77.5m, line.NameConfidence);
        Assert.Equal(0m, line.QuantityConfidence);
        Assert.Equal(0m, line.UnitCostConfidence);
    }

    [Fact]
    public void NonNumericTokensInNumericBands_YieldNullValueAndZeroConfidence()
    {
        var page = Page([
            .. GenericHeader(),
            Word("Harina", 40, 130),
            Word("N/A", 200, 130, confidence: 99)]);

        var line = Assert.Single(_parser.Parse([page], template: null));
        Assert.Equal("Harina", line.Name);
        Assert.Null(line.Quantity);
        Assert.Null(line.UnitCost);
        Assert.Equal(0m, line.QuantityConfidence);
        Assert.Equal(0m, line.UnitCostConfidence);
    }

    [Fact]
    public void FieldConfidence_IsMinimumOfContributingWords_WithOneDecimal()
    {
        var page = Page([
            .. GenericHeader(),
            Word("Café", 40, 130, confidence: 88.0),
            Word("Molido", 90, 130, confidence: 72.25),
            Word("2", 200, 130, confidence: 87.96),
            Word("4,50", 340, 130, confidence: 64.44)]);

        var line = Assert.Single(_parser.Parse([page], template: null));

        Assert.Equal("Café Molido", line.Name);
        Assert.Equal(72.3m, line.NameConfidence);       // min(88.0, 72.25) → 72.3 (AwayFromZero)
        Assert.Equal(88.0m, line.QuantityConfidence);   // 87.96 → 88.0
        Assert.Equal(64.4m, line.UnitCostConfidence);   // 64.44 → 64.4
    }

    [Fact]
    public void EmptyPage_ProducesNoLines()
    {
        Assert.Empty(_parser.Parse([Page()], template: null));
    }

    [Fact]
    public void MultiPage_ConcatenatesLinesInPageOrder()
    {
        var page1 = Page([.. GenericHeader(), .. DataRow("Uno", "1", "1,10", 130)]);
        var page2 = Page([.. GenericHeader(), .. DataRow("Dos", "2", "2,20", 130)]);

        var lines = _parser.Parse([page1, page2], template: null);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Uno", lines[0].Name);
        Assert.Equal(1.10m, lines[0].UnitCost);
        Assert.Equal("Dos", lines[1].Name);
        Assert.Equal(2.20m, lines[1].UnitCost);
    }

    [Fact]
    public void RepeatedHeaderOnLaterPage_IsSkippedAsData()
    {
        var page1 = Page([.. GenericHeader(), .. DataRow("Uno", "1", "1,10", 130)]);
        var page2 = Page([.. GenericHeader(), .. DataRow("Dos", "2", "2,20", 130)]);

        var lines = _parser.Parse([page1, page2], template: null);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Dos", lines[1].Name);
        Assert.DoesNotContain(lines, line => line.Name == "Descripción");
    }

    [Fact]
    public void PageWithoutHeader_ReusesPreviousAnchors()
    {
        var page1 = Page([.. GenericHeader(), .. DataRow("Uno", "1", "1,10", 130)]);
        var page2 = Page(
            Word("Dos", 40, 150),
            Word("5", 200, 150),
            Word("1,10", 340, 150));

        var lines = _parser.Parse([page1, page2], template: null);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Dos", lines[1].Name);
        Assert.Equal(5m, lines[1].Quantity);
        Assert.Equal(1.10m, lines[1].UnitCost);
    }

    [Fact]
    public void PageWithoutTemplateOrGenericHeader_ProducesNoLines()
    {
        var template = new SupplierColumnMappingDto(null, null, "Concepto", "Bultos", "Valor");
        var page = Page(
            Word("FACTURA", 40, 100),
            Word("PROVEEDOR", 140, 100),
            Word("12345", 300, 100));

        Assert.Empty(_parser.Parse([page], template));
        Assert.Empty(_parser.Parse([page], template: null));
    }

    [Fact]
    public void HeaderRow_IsNotEmittedAsData()
    {
        var page = Page([.. GenericHeader(), .. DataRow("Café", "2", "4,50", 130)]);

        var line = Assert.Single(_parser.Parse([page], template: null));

        Assert.Equal("Café", line.Name);
        Assert.Equal(2m, line.Quantity);
    }

    [Fact]
    public void TemplateMappedCodeAndBarcodeBands_AreExtracted()
    {
        var template = new SupplierColumnMappingDto("Barras", "Código", "Descripción", "Cantidad", "Precio");
        var page = Page(
            Word("Código", 40, 100),
            Word("Barras", 150, 100),
            Word("Descripción", 260, 100),
            Word("Cantidad", 420, 100),
            Word("Precio", 540, 100),
            Word("SUP-1", 40, 130),
            Word("7591234567890", 150, 130),
            Word("Café", 260, 130),
            Word("2", 420, 130),
            Word("4,50", 540, 130));

        var line = Assert.Single(_parser.Parse([page], template));

        Assert.Equal("SUP-1", line.SupplierCode);
        Assert.Equal("7591234567890", line.Barcode);
        Assert.Equal("Café", line.Name);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4.50m, line.UnitCost);
    }

    [Fact]
    public void RowsAboveDetectedHeader_AreNotParsedAsData()
    {
        // Cabecera del proveedor (RIF/número): una vez anclado el encabezado no produce filas.
        var page = Page([
            Word("PROVEEDOR", 40, 70),
            Word("12345", 200, 70),
            .. GenericHeader(),
            .. DataRow("Café", "2", "4,50", 130)]);

        var line = Assert.Single(_parser.Parse([page], template: null));
        Assert.Equal("Café", line.Name);
    }
}
