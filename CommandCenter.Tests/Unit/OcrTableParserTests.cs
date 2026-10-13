using Core.Common;
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
        // 8.147-T9/D2: punto solo con grupos de exactamente 3 dígitos = separador de miles es-VE.
        Assert.Equal(4250m, lines[2].UnitCost);
        Assert.Equal(365m, lines[3].Quantity);
        Assert.Equal(1234.56m, lines[3].UnitCost);
        // Banda numérica partida en dos palabras: "$" + "4.250" se unen antes de parsear (miles es-VE).
        Assert.Equal(4250m, lines[4].Quantity);
        Assert.Equal(1234.56m, lines[4].UnitCost);
    }

    [Fact]
    public void DotThousandsHeuristic_ParsesEsVeConventionLikeFilePath()
    {
        var page = Page([
            .. GenericHeader(),
            .. DataRow("Uno", "4.250", "1.234.567", 130),
            .. DataRow("Dos", "4.25", "4.2500", 160),
            .. DataRow("Tres", "0.500", "-4.250", 190),
            .. DataRow("Cuatro", ".50", "0.25", 220)]);

        var lines = _parser.Parse([page], template: null);

        Assert.Equal(4, lines.Count);
        Assert.Equal(4250m, lines[0].Quantity);
        Assert.Equal(1234567m, lines[0].UnitCost);
        Assert.Equal(4.25m, lines[1].Quantity);
        Assert.Equal(4.25m, lines[1].UnitCost);
        // 8.163: cero inicial = decimal ("0.500" → 0,5); en es-VE los miles no se escriben con
        // cero delante.
        Assert.Equal(0.5m, lines[2].Quantity);
        Assert.Equal(-4250m, lines[2].UnitCost);
        // 8.147-T9/D2: sin dígito antes del primer punto no hay regla de miles → decimal puro.
        Assert.Equal(0.5m, lines[3].Quantity);
        Assert.Equal(0.25m, lines[3].UnitCost);
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

    [Fact]
    public void RealFormatCaseA_SevenColumnsWithNoise_ResolvesQuantityAndCost()
    {
        // 8.162/8.163: layout real COMERCIAL M Y E (matriz de punto) con los artefactos que
        // produce el OCR en la práctica: cantidad con comilla de raya manuscrita (“72.00), precio
        // con punto en vez de coma (0.614) y filas de pie (rayas/perforaciones, Nota legal,
        // Sub-Total). Con mapeo de 3 columnas deben quedar EXACTAMENTE las dos líneas de ítems.
        var template = new SupplierColumnMappingDto(null, null, "DESCRIPCION", "CANT.", "P.U.");
        var page = Page(
            Word("CANT.", 40, 100),
            Word("UNIDAD", 140, 100),
            Word("DESCRIPCION", 260, 100),
            Word("%", 480, 100, width: 10),
            Word("ALIC", 500, 100),
            Word("P.U.", 560, 100),
            Word("%", 650, 100, width: 10),
            Word("DSCTO", 670, 100),
            Word("SUBTTL", 780, 100),
            Word("“72.00", 40, 130),
            Word("UND", 150, 130),
            Word("TOALLAS", 260, 130),
            Word("ALIVE", 340, 130),
            Word("DIURNA", 420, 130),
            Word("16,00%", 500, 130),
            Word("0.614", 560, 130),
            Word("0%", 660, 130),
            Word("44,265", 780, 130),
            Word("4,00", 40, 160),
            Word("PAQ", 150, 160),
            Word("VELAS", 260, 160),
            Word("BIPA", 340, 160),
            Word("0,00%", 500, 160),
            Word("10,529", 560, 160),
            Word("0%", 660, 160),
            Word("42,119", 780, 160),
            Word("¥", 704, 190),
            Word("A,", 2725, 190),
            Word("Nota:", 436, 220),
            Word("Sub-Total", 1673, 220),
            Word("430,689", 2210, 220),
            Word("TOTAL", 1656, 250),
            Word("FINAL:", 1796, 250),
            Word("306,91", 2224, 250));

        var lines = _parser.Parse([page], template);

        Assert.Equal(2, lines.Count);
        Assert.Equal(72.00m, lines[0].Quantity);
        Assert.Equal(0.614m, lines[0].UnitCost);
        Assert.Contains("TOALLAS", lines[0].Name);
        Assert.Equal(4.00m, lines[1].Quantity);
        Assert.Equal(10.529m, lines[1].UnitCost);
        Assert.Contains("VELAS", lines[1].Name);
    }

    [Fact]
    public void RealFormatCaseB_SixColumnsDescBeforeUnit_ResolvesQuantityAndCost()
    {
        // 8.162: layout real INVERSIONES NUEVO VIENTO: CANT | DESCRIPCION | UNIDAD | % ALIC |
        // P.U. C/IVA Bs.S | SUBTTL C/IVA Bs.S. El subtotal (banda abierta), el % ALIC y el texto
        // de UNIDAD no deben contaminar el costo; el subtotal tampoco viaja como barras/código.
        var template = new SupplierColumnMappingDto(null, null, "DESCRIPCION", "CANT.", "P.U.");
        var page = Page(
            Word("CANT.", 40, 100),
            Word("DESCRIPCION", 260, 100),
            Word("UNIDAD", 560, 100),
            Word("ALIC", 640, 100),
            Word("P.U.", 720, 100),
            Word("SUBTTL", 840, 100),
            Word("120", 40, 130),
            Word("MAYONESA", 260, 130),
            Word("MAVESA", 380, 130),
            Word("175GR", 480, 130),
            Word("UNIDAD", 560, 130),
            Word("0,00%", 640, 130),
            Word("1.564,22", 720, 130),
            Word("187.706,40", 840, 130),
            Word("5", 40, 160),
            Word("SHAMPOO", 260, 160),
            Word("PANTENE", 380, 160),
            Word("16,00%", 640, 160),
            Word("4.025,37", 720, 160),
            Word("20.126,87", 840, 160));

        var lines = _parser.Parse([page], template);

        Assert.Equal(2, lines.Count);
        Assert.Equal(120m, lines[0].Quantity);
        Assert.Equal(1564.22m, lines[0].UnitCost);
        Assert.Contains("MAYONESA", lines[0].Name);
        Assert.Equal(5m, lines[1].Quantity);
        Assert.Equal(4025.37m, lines[1].UnitCost);
        Assert.Null(lines[0].Barcode);
        Assert.Null(lines[0].SupplierCode);
    }

    [Fact]
    public void FooterTerminator_DoesNotCutProductNamesWithTheWordAsPrefix()
    {
        // 8.163: la detección de pie compara la palabra normalizada completa; un producto como
        // "TOTALCARE" no corta la lectura de la tabla.
        var page = Page([
            .. GenericHeader(),
            .. DataRow("TOTALCARE", "2", "4,50", 130),
            .. DataRow("Café", "1", "1,10", 160)]);

        var lines = _parser.Parse([page], template: null);

        Assert.Equal(2, lines.Count);
        Assert.Equal("TOTALCARE", lines[0].Name);
    }

    [Fact]
    public void CostOnlyRowWithoutIdentifierOrQuantity_IsNotEmitted()
    {
        // 8.170: una fila del pie que solo aporta un costo suelto (sin nombre/código ni cantidad)
        // no se emite; antes bloqueaba el OCR con "Filas OCR incompletas".
        var page = Page([
            .. GenericHeader(),
            .. DataRow("Harina", "2", "4,50", 130),
            Word("97,347", 340, 160)]);

        var line = Assert.Single(_parser.Parse([page], template: null));

        Assert.Equal("Harina", line.Name);
    }

    [Fact]
    public void SplitNumericTokens_WithSign_StillJoinBeforeFallbackSelection()
    {
        // 8.162 regresión: si el bucket completo parsea (número partido en palabras, p. ej. el
        // signo separado), se conserva el comportamiento histórico de unión antes del fallback.
        var page = Page([
            .. GenericHeader(),
            Word("Café", 40, 130),
            Word("-", 200, 130, width: 10),
            Word("4.250", 230, 130),
            Word("1,10", 340, 130)]);

        var line = Assert.Single(_parser.Parse([page], template: null));

        Assert.Equal(-4250m, line.Quantity);
        Assert.Equal(1.10m, line.UnitCost);
    }

    [Theory]
    [InlineData("MANTEQUILLA MAVESA 36%250GR", "MANTEQUILLA MAVESA 36X250GR")]
    [InlineData("CAFE MADRID 1%5", "CAFE MADRID 1X5")]
    [InlineData("SERVILLETAS 10%20 UNI", "SERVILLETAS 10X20 UNI")]
    [InlineData("VASOS PLASTICOS 10420 UNI", "VASOS PLASTICOS 10X20 UNI")]
    [InlineData("HARINA 1440 UND", "HARINA 1X40 UND")]
    [InlineData("AVENA 400GR", "AVENA 400GR")]
    [InlineData("ATUN 140GR", "ATUN 140GR")]
    [InlineData("REFRESCO 1400GR", "REFRESCO 1400GR")]
    [InlineData("LECHE DESCREMADA 2% 1L", "LECHE DESCREMADA 2% 1L")]
    public void SanitizeProductName_NormalizesOcrPackMultipliers_WithoutFalsePositives(
        string rawName,
        string expectedName)
    {
        Assert.Equal(expectedName, OcrConfidence.SanitizeProductName(rawName));

        var page = Page([
            .. GenericHeader(),
            .. DataRow(rawName, "2", "4,50", 130)]);

        var line = Assert.Single(_parser.Parse([page], template: null));
        Assert.Equal(expectedName, line.Name);
    }

    [Fact]
    public void CustomHeaderAndFooterAnchorKeywords_OverrideTableStartAndStopAtCustomFooter()
    {
        var ocrTemplate = new SupplierOcrTemplateDto(
            HeaderAnchorKeywords: "RENGLON, DETALLE",
            FooterAnchorKeywords: "FIN DE RENGLONES, OBSERVACIONES");

        var page = Page(
            Word("DESCRIPCION", 40, 70),
            Word("CANTIDAD", 200, 70),
            Word("PRECIO", 340, 70),
            Word("FILA PRE-TABLA", 40, 85),
            Word("99", 200, 85),
            Word("9,99", 340, 85),
            Word("RENGLON", 40, 100),
            Word("DETALLE", 140, 100),
            Word("CANTIDAD", 320, 100),
            Word("PRECIO", 460, 100),
            Word("HARINA PAN", 140, 130),
            Word("5", 320, 130),
            Word("1,20", 460, 130),
            Word("FIN", 40, 160),
            Word("DE", 90, 160),
            Word("RENGLONES", 140, 160),
            Word("FILA IGNORADA", 140, 190),
            Word("9", 320, 190),
            Word("9,99", 460, 190));

        var line = Assert.Single(_parser.Parse([page], template: null, ocrTemplate));
        Assert.Equal("HARINA PAN", line.Name);
        Assert.Equal(5m, line.Quantity);
        Assert.Equal(1.20m, line.UnitCost);
    }

    [Fact]
    public void PositionalColumnMapping_AssignsFieldsByOneBasedHorizontalOrder()
    {
        var ocrTemplate = new SupplierOcrTemplateDto(
            HeaderAnchorKeywords: "COL1",
            SupplierCodeColumnPosition: 1,
            QuantityColumnPosition: 2,
            ProductColumnPosition: 3,
            UnitCostColumnPosition: 4);

        var page = Page(
            Word("COL1", 40, 100),
            Word("COL2", 140, 100),
            Word("DESCRIPCION", 260, 100),
            Word("COL4", 440, 100),
            Word("COD-99", 40, 130),
            Word("12", 140, 130),
            Word("AZUCAR", 260, 130),
            Word("3,50", 440, 130));

        var line = Assert.Single(_parser.Parse([page], template: null, ocrTemplate));
        Assert.Equal("COD-99", line.SupplierCode);
        Assert.Equal(12m, line.Quantity);
        Assert.Equal("AZUCAR", line.Name);
        Assert.Equal(3.50m, line.UnitCost);
    }

    [Fact]
    public void DecimalSeparatorRules_CommaAndDotRulesParseExplicitConventions()
    {
        var commaTemplate = new SupplierOcrTemplateDto(
            DecimalSeparator: OcrDecimalSeparatorRule.Comma);
        var dotTemplate = new SupplierOcrTemplateDto(
            DecimalSeparator: OcrDecimalSeparatorRule.Dot);

        var pageComma = Page([
            .. GenericHeader(),
            .. DataRow("Item Coma", "1.200,50", "4.500,25", 130)
        ]);
        var commaLine = Assert.Single(_parser.Parse([pageComma], template: null, commaTemplate));
        Assert.Equal(1200.50m, commaLine.Quantity);
        Assert.Equal(4500.25m, commaLine.UnitCost);

        var pageDot = Page([
            .. GenericHeader(),
            .. DataRow("Item Punto", "1,200.50", "4.250", 130)
        ]);
        var dotLine = Assert.Single(_parser.Parse([pageDot], template: null, dotTemplate));
        Assert.Equal(1200.50m, dotLine.Quantity);
        Assert.Equal(4.25m, dotLine.UnitCost);
    }

    [Fact]
    public void CostAmountRule_LineTotal_DividesTotalByQuantityAndRoundsToFourDecimals()
    {
        var ocrTemplate = new SupplierOcrTemplateDto(
            CostAmountRule: OcrCostAmountRule.LineTotal);

        var page = Page([
            .. GenericHeader(),
            .. DataRow("Bulto Arroz", "3", "10,00", 130)
        ]);

        var line = Assert.Single(_parser.Parse([page], template: null, ocrTemplate));
        Assert.Equal(3m, line.Quantity);
        Assert.Equal(3.3333m, line.UnitCost);
    }

    [Fact]
    public void CharacterReplacements_ApplyLiteralRulesBeforeNumericAndDescriptionParsing()
    {
        var ocrTemplate = new SupplierOcrTemplateDto(
            CharacterReplacements: new[]
            {
                new OcrCharacterReplacementRuleDto("O", "0"),
                new OcrCharacterReplacementRuleDto("$", string.Empty)
            });

        var page = Page([
            .. GenericHeader(),
            .. DataRow("CAFE 5OOGR", "1O", "$4,5O", 130)
        ]);

        var line = Assert.Single(_parser.Parse([page], template: null, ocrTemplate));
        Assert.Equal("CAFE 500GR", line.Name);
        Assert.Equal(10m, line.Quantity);
        Assert.Equal(4.50m, line.UnitCost);
    }

    [Theory]
    [InlineData("HARINA PAN 1X40 UND", "AUTO", 40)]
    [InlineData("MANTEQUILLA MAVESA 36X250GR", "AUTO", 36)]
    [InlineData("REFRESCO BTO X 24", "BTO_X_N", 24)]
    [InlineData("GALLETAS CAJA 12 UND", "CAJA_N_UND", 12)]
    [InlineData("ATUN PACK=18 LATAS", @"PACK=(\d+)", 18)]
    public void FactorExtractionPattern_ExtractsPackMultipliersAcrossAutoPresetsAndCustomRegex(
        string description,
        string patternKey,
        decimal expectedFactor)
    {
        var ocrTemplate = new SupplierOcrTemplateDto
        {
            FactorExtractionPattern = patternKey
        };

        var page = Page([
            .. GenericHeader(),
            .. DataRow(description, "2", "20,00", 130)
        ]);

        var line = Assert.Single(_parser.Parse([page], template: null, ocrTemplate));
        Assert.Equal(expectedFactor, line.ExtractedConversionFactor);
    }
}

