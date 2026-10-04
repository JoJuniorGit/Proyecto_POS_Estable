using ClosedXML.Excel;
using Core.DTOs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public partial class SupplierInvoiceService
{
    public Task<List<string>> ReadHeadersAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var extension = Path.GetExtension(filePath);
        EnsureSupportedExtension(extension);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadHeaders(stream, extension, cancellationToken);
        }, cancellationToken);
    }

    public Task<List<StageLineDto>> ParseFileWithMappingAsync(
        string filePath,
        SupplierColumnMappingDto columnMapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(columnMapping);
        var extension = Path.GetExtension(filePath);
        EnsureSupportedExtension(extension);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ParseFile(stream, extension, columnMapping, cancellationToken);
        }, cancellationToken);
    }

    public Task<List<string>> ReadHeadersAsync(
        Stream content,
        string extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureSupportedExtension(extension);
        return Task.Run(() => ReadHeaders(content, extension, cancellationToken), cancellationToken);
    }

    public Task<List<StageLineDto>> ParseFileWithMappingAsync(
        Stream content,
        string extension,
        SupplierColumnMappingDto columnMapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(columnMapping);
        EnsureSupportedExtension(extension);
        return Task.Run(() => ParseFile(content, extension, columnMapping, cancellationToken), cancellationToken);
    }

    private static List<string> ReadHeaders(Stream content, string extension, CancellationToken cancellationToken)
    {
        var rows = ReadRows(content, extension, cancellationToken);
        return rows.Count == 0 ? [] : rows[0].ToList();
    }

    private static List<StageLineDto> ParseFile(
        Stream content,
        string extension,
        SupplierColumnMappingDto columnMapping,
        CancellationToken cancellationToken)
    {
        var rows = ReadRows(content, extension, cancellationToken);
        if (rows.Count < 2)
        {
            throw new InvalidDataException("El archivo no contiene filas de factura legibles.");
        }

        var headers = rows[0];
        var barcodeIndex = FindColumn(headers, columnMapping.BarcodeColumnName, required: false);
        var supplierCodeIndex = FindColumn(headers, columnMapping.SupplierCodeColumnName, required: false);
        var nameIndex = FindColumn(headers, columnMapping.NameColumnName, required: true);
        var quantityIndex = FindColumn(headers, columnMapping.QuantityColumnName, required: true);
        var unitCostIndex = FindColumn(headers, columnMapping.UnitCostColumnName, required: true);
        var parsedLines = new List<StageLineDto>();

        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[rowIndex];
            var barcode = GetCell(row, barcodeIndex);
            var supplierCode = GetCell(row, supplierCodeIndex);
            var name = GetCell(row, nameIndex);
            if (string.IsNullOrWhiteSpace(barcode) &&
                string.IsNullOrWhiteSpace(supplierCode) &&
                string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var quantity = ParseRequiredDecimal(GetCell(row, quantityIndex), "cantidad", rowIndex + 1);
            var unitCost = ParseRequiredDecimal(GetCell(row, unitCostIndex), "costo unitario", rowIndex + 1);
            parsedLines.Add(new StageLineDto(
                supplierCode,
                barcode,
                name,
                quantity,
                unitCost));
        }

        if (parsedLines.Count == 0)
        {
            throw new InvalidDataException("El archivo no contiene filas de factura legibles.");
        }

        return parsedLines;
    }

    private static List<string[]> ReadRows(
        Stream content,
        string extension,
        CancellationToken cancellationToken)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var normalizedExtension = NormalizeExtension(extension);
        return normalizedExtension switch
        {
            ".csv" => ReadCsvRows(content),
            ".xlsx" => ReadWorkbookRows(content, cancellationToken),
            ".xml" => ReadXmlRows(content, cancellationToken),
            _ => throw new NotSupportedException($"El formato de archivo '{extension}' no está soportado.")
        };
    }

    private static List<string[]> ReadCsvRows(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, true, 1024, leaveOpen: true);
        var sourceLines = reader.ReadToEnd()
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var headerLineIndex = Array.FindIndex(sourceLines, line => !string.IsNullOrWhiteSpace(line));
        if (headerLineIndex < 0)
        {
            return [];
        }

        var headerLine = sourceLines[headerLineIndex].TrimStart('\uFEFF');
        var delimiter = DetectDelimiter(headerLine);
        var rows = new List<string[]> { ParseCsvLine(headerLine, delimiter) };
        for (var index = headerLineIndex + 1; index < sourceLines.Length; index++)
        {
            var sourceLine = sourceLines[index];
            if (string.IsNullOrWhiteSpace(sourceLine))
            {
                continue;
            }

            rows.Add(ParseCsvLine(sourceLine, delimiter));
        }

        return rows;
    }

    private static List<string[]> ReadWorkbookRows(Stream content, CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook(content);
        var worksheet = workbook.Worksheets.FirstOrDefault();
        var headerRow = worksheet?.FirstRowUsed();
        if (worksheet is null || headerRow is null)
        {
            return [];
        }

        var firstColumn = headerRow.FirstCellUsed()?.Address.ColumnNumber ?? 1;
        var lastColumn = headerRow.LastCellUsed()?.Address.ColumnNumber ?? firstColumn;
        var headers = Enumerable.Range(firstColumn, lastColumn - firstColumn + 1)
            .Select(column => headerRow.Cell(column).GetString().Trim())
            .ToArray();
        var rows = new List<string[]> { headers };

        foreach (var row in worksheet.RowsUsed().Where(row => row.RowNumber() > headerRow.RowNumber()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(Enumerable.Range(firstColumn, lastColumn - firstColumn + 1)
                .Select(column => GetCellString(row.Cell(column)))
                .ToArray());
        }

        return rows;
    }

    private static char DetectDelimiter(string headerLine)
    {
        var semicolons = CountOutsideQuotes(headerLine, ';');
        var commas = CountOutsideQuotes(headerLine, ',');
        return semicolons >= commas && semicolons > 0 ? ';' : ',';
    }

    private static int CountOutsideQuotes(string value, char target)
    {
        var count = 0;
        var inQuotes = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
            {
                if (inQuotes && index + 1 < value.Length && value[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                inQuotes = !inQuotes;
            }
            else if (!inQuotes && value[index] == target)
            {
                count++;
            }
        }

        return count;
    }

    private static string[] ParseCsvLine(string line, char delimiter)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (!inQuotes && character == delimiter)
            {
                values.Add(value.ToString().Trim());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }

        values.Add(value.ToString().Trim());
        return values.ToArray();
    }

    private static int FindColumn(IReadOnlyList<string> headers, string? columnName, bool required)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            if (required)
            {
                throw new InvalidDataException("Falta asignar una columna obligatoria para la factura.");
            }

            return -1;
        }

        for (var index = 0; index < headers.Count; index++)
        {
            if (string.Equals(headers[index].Trim(), columnName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        if (required)
        {
            throw new InvalidDataException($"No se encontró la columna obligatoria '{columnName}' en el archivo.");
        }

        return -1;
    }

    private static string? GetCell(IReadOnlyList<string> row, int columnIndex) =>
        columnIndex >= 0 && columnIndex < row.Count && !string.IsNullOrWhiteSpace(row[columnIndex])
            ? row[columnIndex].Trim()
            : null;

    private static decimal ParseRequiredDecimal(string? value, string fieldLabel, int rowNumber)
    {
        if (TryParseDecimal(value, out var result))
        {
            return result;
        }

        throw new InvalidDataException($"La fila {rowNumber} no contiene una {fieldLabel} válida.");
    }

    /// <summary>
    /// 8.147-T9/D2: parseo tolerante es-VE/latino, espejo exacto entre la ruta tabular del cliente
    /// y la ruta OCR. Símbolos de moneda y espacios fuera; con coma y punto presentes el último es
    /// el separador decimal; coma sola = separador decimal; punto(s) solos = separador de miles
    /// cuando cada grupo posterior a un punto tiene exactamente 3 dígitos y hay al menos un dígito
    /// antes del primer punto ("4.250" → 4250, "1.234.567" → 1234567). Puntos con 1–2 o 4+ dígitos
    /// después quedan decimales ("4.25" → 4.25, "4.2500" → 4.25). Trade-off aceptado: una cantidad
    /// de 3 decimales escrita con punto se lee como miles; es-VE escribe esos decimales con coma.
    /// </summary>
    private static bool TryParseDecimal(string? value, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim()
            .Replace("$", string.Empty, StringComparison.Ordinal)
            .Replace("€", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        var commaIndex = normalized.LastIndexOf(',');
        var dotIndex = normalized.LastIndexOf('.');

        if (commaIndex >= 0 && dotIndex >= 0)
        {
            if (commaIndex > dotIndex)
            {
                normalized = normalized.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
            }
            else
            {
                normalized = normalized.Replace(",", string.Empty, StringComparison.Ordinal);
            }
        }
        else if (commaIndex >= 0)
        {
            normalized = normalized.Replace(',', '.');
        }
        else if (dotIndex >= 0 && IsThousandsSeparatedByDots(normalized))
        {
            normalized = normalized.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        return decimal.TryParse(
            normalized,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out result);
    }

    /// <summary>
    /// 8.147-T9/D2: es-VE usa el punto como separador de miles y la coma como decimal. Un valor con
    /// puntos solos es formato de miles cuando cada grupo posterior a un punto tiene exactamente
    /// 3 dígitos y hay un dígito inmediatamente antes del primer punto.
    /// </summary>
    private static bool IsThousandsSeparatedByDots(string value)
    {
        var firstDot = value.IndexOf('.');
        if (firstDot <= 0 || !char.IsAsciiDigit(value[firstDot - 1]))
        {
            return false;
        }

        var groupLength = 0;
        for (var index = firstDot + 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '.')
            {
                if (groupLength != 3)
                {
                    return false;
                }

                groupLength = 0;
            }
            else if (char.IsAsciiDigit(character))
            {
                groupLength++;
            }
            else
            {
                return false;
            }
        }

        return groupLength == 3;
    }

    private static string GetCellString(IXLCell cell) =>
        cell.DataType == XLDataType.Number
            ? cell.GetValue<decimal>().ToString(CultureInfo.InvariantCulture)
            : cell.GetString().Trim();

    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        return trimmed.StartsWith(".", StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : $".{trimmed.ToLowerInvariant()}";
    }

    private static void EnsureSupportedExtension(string extension)
    {
        var normalized = NormalizeExtension(extension);
        if (normalized is not ".xlsx" and not ".csv" and not ".xml")
        {
            throw new NotSupportedException($"El formato de archivo '{extension}' no está soportado.");
        }
    }

    private static string NormalizeHeader(string header) =>
        new(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
