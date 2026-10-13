using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

/// <summary>
/// 8.177-T2 (S3): exportación del Kardex (CSV / XLSX / PDF) con encabezado legal. Todos los
/// formatos exportan TODAS las filas filtradas (sin paginar), con la razón social configurada
/// (<c>CompanyLegalName</c>), fecha/hora de generación UTC y local, usuario exportador y el
/// resumen de los filtros aplicados.
/// </summary>
public partial class StockKardexService
{
    private static readonly string[] SupportedExportFormats = { "csv", "xlsx", "pdf" };

    private static readonly string[] ExportColumnHeaders =
    {
        "Fecha UTC", "Fecha local", "Fuente", "Producto ID", "SKU", "Producto",
        "Stock anterior", "Delta", "Stock resultante", "Tipo origen", "Motivo",
        "Detalle", "Costo unitario USD", "Valor total USD", "Ejecutor",
        "Autorizador", "Venta ID", "Factura proveedor ID", "Observaciones"
    };

    public async Task<byte[]> ExportAsync(
        StockKardexFilterDto filter,
        string format,
        CancellationToken cancellationToken = default)
    {
        var normalizedFormat = (format ?? string.Empty).Trim().ToLowerInvariant();
        if (!SupportedExportFormats.Contains(normalizedFormat))
        {
            throw new ArgumentException(
                $"Formato de exportación no soportado: '{format}'. Formatos válidos: csv, xlsx, pdf.",
                nameof(format));
        }

        filter ??= new StockKardexFilterDto();

        var query = ApplySqlFilters(BuildUnionQuery(), filter);
        var rows = RequiresPostClassification(filter)
            ? await LoadClassifiedRowsAsync(query, filter, cancellationToken)
            : await LoadAllFinalizedAsync(query, cancellationToken);

        var companyName = await ResolveCompanyLegalNameAsync();
        var generatedUtc = DateTime.UtcNow;
        var timeZone = await ResolveTimeZoneAsync(cancellationToken);
        var generatedLocal = ConvertToLocal(generatedUtc, timeZone);
        var exportedBy = string.IsNullOrWhiteSpace(_currentUserService?.UserId)
            ? "Sistema"
            : _currentUserService!.UserId!;
        var filterSummary = BuildFilterSummary(filter, timeZone);

        return normalizedFormat switch
        {
            "csv" => BuildCsvExport(rows, companyName, generatedUtc, generatedLocal, exportedBy, filterSummary, timeZone),
            "xlsx" => BuildXlsxExport(rows, companyName, generatedUtc, generatedLocal, exportedBy, filterSummary, timeZone),
            _ => BuildPdfExport(rows, companyName, generatedUtc, generatedLocal, exportedBy, filterSummary, timeZone)
        };
    }

    private async Task<string> ResolveCompanyLegalNameAsync()
    {
        var configured = await _settingsService.GetSettingAsync(CompanyLegalNameKey);
        return string.IsNullOrWhiteSpace(configured) ? DefaultCompanyLegalName : configured.Trim();
    }

    private async Task<TimeZoneInfo> ResolveTimeZoneAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);
        }
        catch
        {
            // Fallback documentado: si el provider falla (p. ej. setting corrupto), la hora local
            // cae a la zona del sistema operativo antes que abortar la exportación.
            return TimeZoneHelper.GetVenezuelaTimeZone();
        }
    }

    private static DateTime ConvertToLocal(DateTime utc, TimeZoneInfo timeZone)
    {
        var utcFixed = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        try
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utcFixed, timeZone);
        }
        catch
        {
            return utcFixed.ToLocalTime();
        }
    }

    private static string BuildFilterSummary(StockKardexFilterDto filter, TimeZoneInfo timeZone)
    {
        var parts = new List<string>();

        if (filter.DateFromUtc.HasValue)
        {
            parts.Add($"Desde (local): {ConvertToLocal(filter.DateFromUtc.Value, timeZone):dd/MM/yyyy HH:mm:ss}");
        }

        if (filter.DateToUtc.HasValue)
        {
            parts.Add($"Hasta (local): {ConvertToLocal(filter.DateToUtc.Value, timeZone):dd/MM/yyyy HH:mm:ss}");
        }

        if (!string.IsNullOrWhiteSpace(filter.UserId))
        {
            parts.Add($"Usuario: {filter.UserId.Trim()}");
        }

        if (filter.ProductId.HasValue)
        {
            parts.Add($"Producto ID: {filter.ProductId.Value}");
        }

        if (filter.ReasonCodes is { Count: > 0 })
        {
            parts.Add($"Motivos: {string.Join(", ", filter.ReasonCodes)}");
        }

        if (filter.OriginTypes is { Count: > 0 })
        {
            parts.Add($"Orígenes: {string.Join(", ", filter.OriginTypes)}");
        }

        if (filter.Direction != StockKardexDirection.All)
        {
            parts.Add($"Sentido: {filter.Direction}");
        }

        return parts.Count == 0 ? "Sin filtros (todos los movimientos)" : string.Join(" | ", parts);
    }

    private static string DescribeSource(string source) =>
        string.Equals(source, "Archived", StringComparison.Ordinal) ? "Archivo" : "Activo";

    private static string DescribeClassification(StockKardexRowDto row)
    {
        var parts = new List<string>(2);
        if (row.OriginType.HasValue)
        {
            parts.Add(row.OriginType.Value.ToString());
        }

        if (row.ReasonCode.HasValue)
        {
            parts.Add(row.ReasonCode.Value.ToString());
        }

        return parts.Count == 0 ? "Sin clasificar" : string.Join("/", parts);
    }

    private static byte[] BuildCsvExport(
        IReadOnlyList<StockKardexRowDto> rows,
        string companyName,
        DateTime generatedUtc,
        DateTime generatedLocal,
        string exportedBy,
        string filterSummary,
        TimeZoneInfo timeZone)
    {
        var sb = new StringBuilder();
        sb.AppendLine(EscapeCsvField("KARDEX OFICIAL DE MOVIMIENTOS DE INVENTARIO"));
        sb.AppendLine($"Razón Social;{EscapeCsvField(companyName)}");
        sb.AppendLine($"Generado (UTC);{EscapeCsvField(generatedUtc.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture))}");
        sb.AppendLine($"Generado (hora local);{EscapeCsvField(generatedLocal.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture))}");
        sb.AppendLine($"Exportado por;{EscapeCsvField(exportedBy)}");
        sb.AppendLine($"Filtros aplicados;{EscapeCsvField(filterSummary)}");
        sb.AppendLine();
        sb.AppendLine(string.Join(";", ExportColumnHeaders));

        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(";", new[]
            {
                row.MovementDateUtc.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                ConvertToLocal(row.MovementDateUtc, timeZone).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                DescribeSource(row.Source),
                row.ProductId.ToString(CultureInfo.InvariantCulture),
                EscapeCsvField(row.SKU),
                EscapeCsvField(row.ProductName),
                row.StockBefore.ToString("0.###", CultureInfo.InvariantCulture),
                row.QuantityChange.ToString("0.###", CultureInfo.InvariantCulture),
                row.NewStockLevel.ToString("0.###", CultureInfo.InvariantCulture),
                row.OriginType?.ToString() ?? "Sin clasificar",
                row.ReasonCode?.ToString() ?? "Sin clasificar",
                EscapeCsvField(row.Reason),
                row.UnitCostUSD?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "",
                row.TotalValueUSD?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
                EscapeCsvField(row.ExecutedByUserId),
                EscapeCsvField(row.AuthorizedByUserId),
                row.SaleId?.ToString(CultureInfo.InvariantCulture) ?? "",
                row.SupplierInvoiceId?.ToString(CultureInfo.InvariantCulture) ?? "",
                EscapeCsvField(row.Observation)
            }));
        }

        // Precedente del repo (InventoryService.Export): separador ';' es-VE y UTF-8 con BOM
        // para que Excel abra el archivo con acentos correctos.
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static byte[] BuildXlsxExport(
        IReadOnlyList<StockKardexRowDto> rows,
        string companyName,
        DateTime generatedUtc,
        DateTime generatedLocal,
        string exportedBy,
        string filterSummary,
        TimeZoneInfo timeZone)
    {
        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Kardex");

        worksheet.Cell(1, 1).Value = "KARDEX OFICIAL DE MOVIMIENTOS DE INVENTARIO";
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;
        worksheet.Range(1, 1, 1, ExportColumnHeaders.Length).Merge();

        WriteXlsxLegalLine(worksheet, 2, "Razón Social:", companyName);
        WriteXlsxLegalLine(worksheet, 3, "Generado (UTC):", generatedUtc.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture));
        WriteXlsxLegalLine(worksheet, 4, "Generado (hora local):", generatedLocal.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture));
        WriteXlsxLegalLine(worksheet, 5, "Exportado por:", exportedBy);
        WriteXlsxLegalLine(worksheet, 6, "Filtros aplicados:", filterSummary);

        const int headerRow = 8;
        for (int col = 0; col < ExportColumnHeaders.Length; col++)
        {
            var cell = worksheet.Cell(headerRow, col + 1);
            cell.Value = ExportColumnHeaders[col];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#1E88E5");
            cell.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
        }

        int rowIndex = headerRow + 1;
        foreach (var row in rows)
        {
            worksheet.Cell(rowIndex, 1).Value = row.MovementDateUtc;
            worksheet.Cell(rowIndex, 1).Style.DateFormat.Format = "dd/MM/yyyy HH:mm:ss";
            worksheet.Cell(rowIndex, 2).Value = ConvertToLocal(row.MovementDateUtc, timeZone);
            worksheet.Cell(rowIndex, 2).Style.DateFormat.Format = "dd/MM/yyyy HH:mm:ss";
            worksheet.Cell(rowIndex, 3).Value = DescribeSource(row.Source);
            worksheet.Cell(rowIndex, 4).Value = row.ProductId;
            worksheet.Cell(rowIndex, 5).Value = row.SKU;
            worksheet.Cell(rowIndex, 6).Value = row.ProductName;
            worksheet.Cell(rowIndex, 7).Value = row.StockBefore;
            worksheet.Cell(rowIndex, 8).Value = row.QuantityChange;
            worksheet.Cell(rowIndex, 9).Value = row.NewStockLevel;
            worksheet.Cell(rowIndex, 10).Value = row.OriginType?.ToString() ?? "Sin clasificar";
            worksheet.Cell(rowIndex, 11).Value = row.ReasonCode?.ToString() ?? "Sin clasificar";
            worksheet.Cell(rowIndex, 12).Value = row.Reason;
            if (row.UnitCostUSD.HasValue)
            {
                worksheet.Cell(rowIndex, 13).Value = row.UnitCostUSD.Value;
            }

            if (row.TotalValueUSD.HasValue)
            {
                worksheet.Cell(rowIndex, 14).Value = row.TotalValueUSD.Value;
            }

            if (!string.IsNullOrWhiteSpace(row.ExecutedByUserId))
            {
                worksheet.Cell(rowIndex, 15).Value = row.ExecutedByUserId;
            }

            if (!string.IsNullOrWhiteSpace(row.AuthorizedByUserId))
            {
                worksheet.Cell(rowIndex, 16).Value = row.AuthorizedByUserId;
            }

            if (row.SaleId.HasValue)
            {
                worksheet.Cell(rowIndex, 17).Value = row.SaleId.Value;
            }

            if (row.SupplierInvoiceId.HasValue)
            {
                worksheet.Cell(rowIndex, 18).Value = row.SupplierInvoiceId.Value;
            }

            if (!string.IsNullOrWhiteSpace(row.Observation))
            {
                worksheet.Cell(rowIndex, 19).Value = row.Observation;
            }

            rowIndex++;
        }

        worksheet.Columns().AdjustToContents();

        using var memoryStream = new MemoryStream();
        workbook.SaveAs(memoryStream);
        return memoryStream.ToArray();
    }

    private static void WriteXlsxLegalLine(ClosedXML.Excel.IXLWorksheet worksheet, int row, string label, string value)
    {
        worksheet.Cell(row, 1).Value = label;
        worksheet.Cell(row, 1).Style.Font.Bold = true;
        worksheet.Cell(row, 2).Value = value;
    }

    private static byte[] BuildPdfExport(
        IReadOnlyList<StockKardexRowDto> rows,
        string companyName,
        DateTime generatedUtc,
        DateTime generatedLocal,
        string exportedBy,
        string filterSummary,
        TimeZoneInfo timeZone)
    {
        var pdfRows = new List<string[]>(rows.Count);
        foreach (var row in rows)
        {
            pdfRows.Add(new[]
            {
                row.MovementDateUtc.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                DescribeSource(row.Source),
                row.SKU,
                row.ProductName,
                row.StockBefore.ToString("0.###", CultureInfo.InvariantCulture),
                row.QuantityChange.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture),
                row.NewStockLevel.ToString("0.###", CultureInfo.InvariantCulture),
                DescribeClassification(row),
                row.UnitCostUSD?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "-",
                row.TotalValueUSD?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-",
                row.ExecutedByUserId ?? "-"
            });
        }

        return StockKardexPdfWriter.Generate(
            companyName, generatedUtc, generatedLocal, exportedBy, filterSummary, pdfRows);
    }

    private static string EscapeCsvField(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return "";
        }

        // 8I-M8 (precedente): neutralizar NUL, espacios iniciales y fórmulas (CWE-1236) — Excel
        // interpreta '=', '+', '-', '@' y prefijos con espacio como inicio de fórmula.
        field = field.Replace("\0", "");
        var trimmedStart = field.TrimStart();
        bool hadLeadingSpace = trimmedStart.Length != field.Length;

        if (hadLeadingSpace
            || field.StartsWith('=') || field.StartsWith('+')
            || field.StartsWith('-') || field.StartsWith('@')
            || field.StartsWith('\t') || field.StartsWith('\r'))
        {
            field = "'" + trimmedStart;
        }

        if (field.Contains(';') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        return field;
    }
}

/// <summary>
/// 8.177-T2 (S3): writer PDF crudo multi-página (sin dependencias nuevas, patrón
/// <c>ClosurePdfGenerator</c>). Cada página repite los encabezados de columna, numera
/// "Página X de Y" y la última hoja agrega el bloque de cierre.
/// </summary>
internal static class StockKardexPdfWriter
{
    private const float PageWidth = 792f;
    private const float PageHeight = 612f;
    private const float Margin = 30f;
    private const float RowHeight = 14f;
    private const int RowsPerPage = 25;

    private readonly record struct PdfColumn(float X, float Width, string Label, bool RightAligned);

    private static readonly PdfColumn[] Columns =
    {
        new(30f, 78f, "FECHA (UTC)", false),
        new(110f, 40f, "FUENTE", false),
        new(152f, 58f, "SKU", false),
        new(212f, 122f, "PRODUCTO", false),
        new(336f, 40f, "ANT.", true),
        new(378f, 40f, "DELTA", true),
        new(420f, 40f, "RES.", true),
        new(462f, 104f, "CLASIFICACIÓN", false),
        new(568f, 48f, "COSTO USD", true),
        new(618f, 50f, "VALOR USD", true),
        new(670f, 92f, "EJECUTOR", false)
    };

    public static byte[] Generate(
        string companyName,
        DateTime generatedUtc,
        DateTime generatedLocal,
        string exportedBy,
        string filterSummary,
        IReadOnlyList<string[]> rows)
    {
        int pageCount = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)RowsPerPage));
        var pages = new List<string>(pageCount);
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            pages.Add(BuildPage(pageIndex, pageCount, rows, companyName, generatedUtc, generatedLocal, exportedBy, filterSummary));
        }

        return Assemble(pages);
    }

    private static string BuildPage(
        int pageIndex,
        int pageCount,
        IReadOnlyList<string[]> rows,
        string companyName,
        DateTime generatedUtc,
        DateTime generatedLocal,
        string exportedBy,
        string filterSummary)
    {
        var sb = new StringBuilder();

        WriteTextLeft(sb, $"KARDEX OFICIAL - {Truncate(companyName, 60)}", Margin, 22, 7f, false, "0.5 0.5 0.5 rg");
        WriteTextRight(sb, $"Página {pageIndex + 1} de {pageCount}", PageWidth - Margin, 22, 7f, false, "0.5 0.5 0.5 rg");

        float tableTop = 462f;
        if (pageIndex == 0)
        {
            sb.AppendLine("0.12 0.11 0.29 rg");
            sb.AppendLine($"{Margin} 545 {PageWidth - 2 * Margin} 40 re f");
            WriteTextLeft(sb, "KARDEX OFICIAL DE MOVIMIENTOS DE INVENTARIO", Margin + 12, 566, 15f, true, "1 1 1 rg");
            WriteTextLeft(sb, "Registro histórico auditable de inventario - movimientos activos + archivo", Margin + 12, 551, 8f, false, "0.65 0.71 0.99 rg");

            float y = 528f;
            WriteTextLeft(sb, $"Razón Social: {Truncate(companyName, 80)}", Margin, y, 9f, true, "0.15 0.2 0.3 rg");
            y -= 12;
            WriteTextLeft(sb, $"Generado (UTC): {generatedUtc:dd/MM/yyyy HH:mm:ss}", Margin, y, 8f, false, "0.2 0.25 0.33 rg");
            y -= 12;
            WriteTextLeft(sb, $"Generado (hora local): {generatedLocal:dd/MM/yyyy HH:mm:ss}", Margin, y, 8f, false, "0.2 0.25 0.33 rg");
            y -= 12;
            WriteTextLeft(sb, $"Exportado por: {Truncate(exportedBy, 40)}", Margin, y, 8f, false, "0.2 0.25 0.33 rg");
            y -= 12;
            WriteTextLeft(sb, $"Filtros: {Truncate(filterSummary, 130)}", Margin, y, 8f, false, "0.2 0.25 0.33 rg");
        }
        else
        {
            tableTop = 560f;
        }

        sb.AppendLine("0.2 0.25 0.33 rg");
        sb.AppendLine($"{Margin} {tableTop} {PageWidth - 2 * Margin} 16 re f");
        foreach (var column in Columns)
        {
            if (column.RightAligned)
            {
                WriteTextRight(sb, column.Label, column.X + column.Width, tableTop + 4, 7f, true, "1 1 1 rg");
            }
            else
            {
                WriteTextLeft(sb, column.Label, column.X, tableTop + 4, 7f, true, "1 1 1 rg");
            }
        }

        int firstRow = pageIndex * RowsPerPage;
        int lastRow = Math.Min(firstRow + RowsPerPage, rows.Count);
        float rowY = tableTop - RowHeight;
        for (int i = firstRow; i < lastRow; i++)
        {
            var cells = rows[i];
            if ((i - firstRow) % 2 == 1)
            {
                sb.AppendLine($"0.95 0.96 0.97 rg {Margin} {rowY} {PageWidth - 2 * Margin} {RowHeight} re f");
            }

            for (int c = 0; c < Columns.Length && c < cells.Length; c++)
            {
                var column = Columns[c];
                var value = Truncate(cells[c], c == 3 ? 24 : 18);
                if (column.RightAligned)
                {
                    WriteTextRight(sb, value, column.X + column.Width, rowY + 4, 7f, false, "0.1 0.1 0.1 rg");
                }
                else
                {
                    WriteTextLeft(sb, value, column.X, rowY + 4, 7f, false, "0.1 0.1 0.1 rg");
                }
            }

            rowY -= RowHeight;
        }

        if (rows.Count == 0 && pageIndex == 0)
        {
            WriteTextLeft(sb, "Sin movimientos para los filtros aplicados.", Margin, tableTop - 24, 9f, false, "0.4 0.4 0.4 rg");
        }

        if (pageIndex == pageCount - 1)
        {
            float closeY = Math.Max(rowY - 16f, 70f);
            sb.AppendLine($"0.8 0.84 0.88 RG 0.5 w {Margin} {closeY} {PageWidth - 2 * Margin} 0 m S");
            WriteTextLeft(
                sb,
                "Documento generado automáticamente por CommandCenter POS - constancia de movimientos de inventario.",
                Margin,
                closeY - 12,
                7f,
                false,
                "0.5 0.5 0.5 rg");
            WriteTextLeft(sb, $"Total de movimientos exportados: {rows.Count}", Margin, closeY - 24, 7f, false, "0.5 0.5 0.5 rg");
        }

        return sb.ToString();
    }

    private static byte[] Assemble(List<string> pageContents)
    {
        int pageCount = pageContents.Count;
        int firstContentObject = 3 + pageCount;
        int fontRegularObject = 3 + 2 * pageCount;
        int fontBoldObject = fontRegularObject + 1;

        var objects = new List<string>(2 + 2 * pageCount + 2)
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj",
            $"2 0 obj\n<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i} 0 R"))}] /Count {pageCount} >>\nendobj"
        };

        for (int i = 0; i < pageCount; i++)
        {
            objects.Add(
                $"{3 + i} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:0.##} {PageHeight:0.##}] " +
                $"/Resources << /Font << /F1 {fontRegularObject} 0 R /F2 {fontBoldObject} 0 R >> >> /Contents {firstContentObject + i} 0 R >>\nendobj");
        }

        for (int i = 0; i < pageContents.Count; i++)
        {
            var content = pageContents[i];
            var contentLength = Encoding.ASCII.GetBytes(content).Length;
            objects.Add($"{firstContentObject + i} 0 obj\n<< /Length {contentLength} >>\nstream\n{content}\nendstream\nendobj");
        }

        objects.Add($"{fontRegularObject} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        objects.Add($"{fontBoldObject} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj");

        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writer.WriteLine("%PDF-1.4");
            writer.Flush();
        }

        var offsets = new List<long>(objects.Count);
        foreach (var obj in objects)
        {
            offsets.Add(ms.Position);
            var objBytes = Encoding.ASCII.GetBytes(obj + "\n");
            ms.Write(objBytes, 0, objBytes.Length);
        }

        long startXRef = ms.Position;
        using (var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writer.WriteLine("xref");
            writer.WriteLine($"0 {objects.Count + 1}");
            writer.WriteLine("0000000000 65535 f ");
            foreach (var objectOffset in offsets)
            {
                writer.WriteLine($"{objectOffset:D10} 00000 n ");
            }

            writer.WriteLine("trailer");
            writer.WriteLine($"<< /Size {objects.Count + 1} /Root 1 0 R >>");
            writer.WriteLine("startxref");
            writer.WriteLine(startXRef);
            writer.WriteLine("%%EOF");
            writer.Flush();
        }

        return ms.ToArray();
    }

    private static void WriteTextLeft(StringBuilder sb, string text, float x, float y, float fontSize, bool bold, string color)
    {
        sb.AppendLine("BT");
        sb.AppendLine(bold ? $"/F2 {fontSize:0.##} Tf" : $"/F1 {fontSize:0.##} Tf");
        sb.AppendLine(color);
        sb.AppendLine($"{x:0.##} {y:0.##} Td");
        sb.AppendLine($"({PdfEscape(text)}) Tj");
        sb.AppendLine("ET");
    }

    private static void WriteTextRight(StringBuilder sb, string text, float rightX, float y, float fontSize, bool bold, string color)
    {
        float width = MeasureTextWidth(text, fontSize);
        WriteTextLeft(sb, text, rightX - width, y, fontSize, bold, color);
    }

    private static float MeasureTextWidth(string text, float fontSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0f;
        }

        float total = 0f;
        float scale = fontSize / 1000f;
        foreach (char c in text)
        {
            float width;
            if (c >= '0' && c <= '9') width = 556f;
            else switch (c)
            {
                case ' ': width = 278f; break;
                case '.': case ',': case ':': case ';': width = 278f; break;
                case '-': case '+': width = 556f; break;
                case '(': case ')': width = 333f; break;
                case 'M': case 'W': width = 833f; break;
                case 'I': case 'i': case 'l': case 't': case '1': width = 278f; break;
                default:
                    width = char.IsUpper(c) ? 667f : 500f;
                    break;
            }

            total += width * scale;
        }

        return total;
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    private static string PdfEscape(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (char c in text)
        {
            switch (c)
            {
                case '(': sb.Append(@"\("); break;
                case ')': sb.Append(@"\)"); break;
                case '\\': sb.Append(@"\\"); break;
                case 'á': sb.Append(@"\341"); break;
                case 'é': sb.Append(@"\351"); break;
                case 'í': sb.Append(@"\355"); break;
                case 'ó': sb.Append(@"\363"); break;
                case 'ú': sb.Append(@"\372"); break;
                case 'ñ': sb.Append(@"\361"); break;
                case 'Ñ': sb.Append(@"\321"); break;
                case 'Á': sb.Append(@"\301"); break;
                case 'É': sb.Append(@"\311"); break;
                case 'Í': sb.Append(@"\315"); break;
                case 'Ó': sb.Append(@"\323"); break;
                case 'Ú': sb.Append(@"\332"); break;
                case '°': sb.Append(@"\260"); break;
                default:
                    if (c < 128) sb.Append(c);
                    else sb.Append('?');
                    break;
            }
        }

        return sb.ToString();
    }
}
