using ClosedXML.Excel;
using Core.DTOs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class ProductImportService : IProductImportService
{
    private readonly HttpClient _httpClient;

    public ProductImportService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> GenerateTemplateAsync(string destinationPath)
    {
        return await Task.Run(() =>
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Plantilla Productos");

            var headers = new[]
            {
                "SKU", "Nombre", "Descripción", "CostoUSD", "MargenDetal%",
                "MargenMayor%", "CantMinMayorista", "HabilitarMayorista",
                "EsFraccionable", "StockActual", "UmbralMinimo"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.AirForceBlue;
                cell.Style.Font.FontColor = XLColor.White;
            }

            // Example Data
            worksheet.Cell(2, 1).Value = "1001";
            worksheet.Cell(2, 2).Value = "Producto Ejemplo";
            worksheet.Cell(2, 3).Value = "Descripción de ejemplo";
            worksheet.Cell(2, 4).Value = 10.00;
            worksheet.Cell(2, 5).Value = 30.00;
            worksheet.Cell(2, 6).Value = 20.00;
            worksheet.Cell(2, 7).Value = 6.000;
            worksheet.Cell(2, 8).Value = "SI";
            worksheet.Cell(2, 9).Value = "NO";
            worksheet.Cell(2, 10).Value = 100;
            worksheet.Cell(2, 11).Value = 5;

            worksheet.Columns().AdjustToContents();
            workbook.SaveAs(destinationPath);
            return destinationPath;
        });
    }

    public async Task<List<string>> ReadHeadersAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            var headers = new List<string>();
            try
            {
                if (filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    var firstLine = File.ReadLines(filePath).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(firstLine))
                    {
                        var delimiter = firstLine.Contains(';') ? ';' : ',';
                        headers = firstLine.Split(delimiter).Select(h => h.Trim('"', ' ')).ToList();
                    }
                    return headers;
                }

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var workbook = new XLWorkbook(stream);
                var worksheet = workbook.Worksheets.First();
                var firstRow = worksheet.FirstRowUsed();
                if (firstRow != null)
                {
                    foreach (var cell in firstRow.CellsUsed())
                    {
                        headers.Add(cell.GetString()?.Trim() ?? string.Empty);
                    }
                }
            }
            catch (Exception)
            {
                // Return empty list if error
            }
            return headers;
        });
    }

    public async Task<List<ProductImportDto>> ParseFileWithMappingAsync(string filePath, Dictionary<string, string> columnMapping, decimal currentExchangeRate)
    {
        return await Task.Run(() =>
        {
            var results = new List<ProductImportDto>();

            try
            {
                if (filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    return ParseCsvWithMapping(filePath, columnMapping);
                }

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var workbook = new XLWorkbook(stream);
                var worksheet = workbook.Worksheets.First();
                var headersRow = worksheet.FirstRowUsed();
                if (headersRow == null) return results;

                var propertyColumnIndices = new Dictionary<string, int>();
                foreach (var cell in headersRow.CellsUsed())
                {
                    var headerName = cell.GetString()?.Trim() ?? string.Empty;
                    if (columnMapping.TryGetValue(headerName, out var propertyName) && propertyName != "Ignore")
                    {
                        propertyColumnIndices[propertyName] = cell.Address.ColumnNumber;
                    }
                }

                var rows = worksheet.RowsUsed().Skip(1);

                foreach (var row in rows)
                {
                    bool hasAnyData = false;
                    foreach (var col in propertyColumnIndices.Values)
                    {
                        if (!row.Cell(col).IsEmpty() && !string.IsNullOrWhiteSpace(row.Cell(col).GetString()))
                        {
                            hasAnyData = true;
                            break;
                        }
                    }
                    if (!hasAnyData) continue;

                    var dto = new ProductImportDto();
                    var errors = new List<string>();

                    try
                    {
                        dto.SKU = GetStringValue(row, propertyColumnIndices, "SKU", "SKU", "Codigo", "Código");
                        dto.Name = GetStringValue(row, propertyColumnIndices, "Name", "Nombre", "Producto");
                        dto.Description = GetStringValue(row, propertyColumnIndices, "Description", "Descripción", "Descripcion");

                        bool parseCost = TryParseDecimal(row, propertyColumnIndices, out var cost, "CostPriceUSD", "Cost (USD)", "CostoUSD", "Costo");
                        bool parseRetailMargin = TryParseDecimal(row, propertyColumnIndices, out var retailMargin, "ProfitMarginRetail", "Retail Margin (%)", "MargenDetal%", "MargenDetal");

                        bool parseWholesaleMargin = TryParseDecimal(row, propertyColumnIndices, out var wholesaleMargin, "ProfitMarginWholesale", "Wholesale Margin (%)", "MargenMayor%", "MargenMayor");
                        bool parseMinWholesaleQty = TryParseDecimal(row, propertyColumnIndices, out var minWholesaleQty, "MinWholesaleQuantity", "Min Wholesale Quantity", "CantMinMayorista");
                        bool hasWholesale = ParseBoolValue(row, propertyColumnIndices, "HasWholesale", "Enable Wholesale", "HabilitarMayorista");
                        bool isFractional = ParseBoolValue(row, propertyColumnIndices, "IsFractional", "Is Fractional", "EsFraccionable");

                        bool parseStock = TryParseDecimal(row, propertyColumnIndices, out var stock, "StockQuantity", "Current Stock", "StockActual", "Stock");
                        bool parseLowStock = TryParseDecimal(row, propertyColumnIndices, out var lowStock, "LowStockThreshold", "Low Stock Threshold", "UmbralMinimo");

                        dto.CostPriceUSD = parseCost ? Math.Max(0m, cost) : 0m;
                        dto.ProfitMarginRetail = parseRetailMargin ? Math.Max(0m, retailMargin) : 0m;
                        dto.ProfitMarginWholesale = parseWholesaleMargin ? Math.Max(0m, wholesaleMargin) : 0m;
                        dto.MinWholesaleQuantity = parseMinWholesaleQty ? Math.Max(0m, minWholesaleQty) : 6.000m;
                        dto.HasWholesale = hasWholesale;
                        dto.IsFractional = isFractional;
                        dto.StockQuantity = parseStock ? Math.Max(0m, stock) : 0m;
                        dto.LowStockThreshold = parseLowStock ? Math.Max(0m, lowStock) : 5m;
                        dto.UnitOfMeasure = dto.IsFractional ? "Kg" : "Und";
                        dto.IsActive = true;

                        // Financial Logic (Calculated from cost and profit margin)
                        dto.PriceRetailUSD = dto.CostPriceUSD > 0 && dto.ProfitMarginRetail > 0
                            ? Math.Ceiling(dto.CostPriceUSD * (1m + dto.ProfitMarginRetail / 100m) * 100m) / 100m
                            : 0m;

                        if (dto.HasWholesale)
                        {
                            dto.PriceWholesaleUSD = dto.CostPriceUSD > 0 && dto.ProfitMarginWholesale > 0
                                ? Math.Ceiling(dto.CostPriceUSD * (1m + dto.ProfitMarginWholesale / 100m) * 100m) / 100m
                                : dto.PriceRetailUSD;
                        }
                        else
                        {
                            dto.PriceWholesaleUSD = dto.PriceRetailUSD;
                            dto.ProfitMarginWholesale = dto.ProfitMarginRetail;
                            dto.MinWholesaleQuantity = 0.000m;
                        }

                        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("El nombre no puede estar vacío.");
                        if (string.IsNullOrWhiteSpace(dto.SKU))
                        {
                            errors.Add("El SKU/Código de barras no puede estar vacío.");
                        }
                        else if (!System.Text.RegularExpressions.Regex.IsMatch(dto.SKU.Trim(), @"^\d+$"))
                        {
                            errors.Add("El SKU/Código de barras debe ser estrictamente un número entero (solo dígitos 0-9).");
                        }

                        if (errors.Any())
                        {
                            dto.IsValid = false;
                            dto.ErrorMessage = string.Join(" | ", errors);
                        }
                        else
                        {
                            dto.IsValid = true;
                            dto.ErrorMessage = string.Empty;
                        }
                    }
                    catch (Exception ex)
                    {
                        dto.IsValid = false;
                        dto.ErrorMessage = $"Error inesperado en la fila: {ex.Message}";
                    }

                    results.Add(dto);
                }
            }
            catch (Exception)
            {
                // General file read error
            }

            return results;
        });
    }

    private List<ProductImportDto> ParseCsvWithMapping(string filePath, Dictionary<string, string> propertyColumnIndicesMap)
    {
        var results = new List<ProductImportDto>();
        var lines = File.ReadAllLines(filePath);
        if (lines.Length <= 1) return results;

        var headerLine = lines[0];
        char delimiter = headerLine.Contains(';') ? ';' : ',';
        var headers = headerLine.Split(delimiter).Select(h => h.Trim()).ToArray();

        var propertyColumnIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
        {
            var headerName = headers[i];
            if (propertyColumnIndicesMap.TryGetValue(headerName, out var propName) && propName != "Ignore")
            {
                propertyColumnIndices[propName] = i;
            }
        }

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(delimiter).Select(p => p.Trim()).ToArray();
            var dto = new ProductImportDto();
            var errors = new List<string>();

            string GetCsvStr(params string[] props)
            {
                foreach (var prop in props)
                {
                    if (propertyColumnIndices.TryGetValue(prop, out int c) && c < parts.Length && !string.IsNullOrWhiteSpace(parts[c]))
                        return parts[c].Trim();
                }
                return string.Empty;
            }

            decimal GetCsvDec(params string[] props)
            {
                foreach (var prop in props)
                {
                    if (propertyColumnIndices.TryGetValue(prop, out int c) && c < parts.Length && decimal.TryParse(parts[c], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal val))
                        return val;
                }
                return 0m;
            }

            int GetCsvInt(params string[] props)
            {
                foreach (var prop in props)
                {
                    if (propertyColumnIndices.TryGetValue(prop, out int c) && c < parts.Length && decimal.TryParse(parts[c], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal decVal))
                        return Convert.ToInt32(Math.Round(decVal));
                }
                return 0;
            }

            bool GetCsvBool(params string[] props)
            {
                foreach (var prop in props)
                {
                    if (propertyColumnIndices.TryGetValue(prop, out int c) && c < parts.Length)
                    {
                        var str = parts[c].Trim();
                        if (str.Equals("true", StringComparison.OrdinalIgnoreCase) || str == "1" || str.Equals("si", StringComparison.OrdinalIgnoreCase) || str.Equals("sí", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                return false;
            }

            dto.SKU = GetCsvStr("SKU", "SKU", "Codigo", "Código");
            dto.Name = GetCsvStr("Name", "Nombre", "Producto");
            dto.Description = GetCsvStr("Description", "Descripción", "Descripcion");

            dto.CostPriceUSD = Math.Max(0m, GetCsvDec("CostPriceUSD", "Cost (USD)", "CostoUSD", "Costo"));
            dto.ProfitMarginRetail = Math.Max(0m, GetCsvDec("ProfitMarginRetail", "Retail Margin (%)", "MargenDetal%", "MargenDetal"));

            dto.ProfitMarginWholesale = Math.Max(0m, GetCsvDec("ProfitMarginWholesale", "Wholesale Margin (%)", "MargenMayor%", "MargenMayor"));
            dto.MinWholesaleQuantity = Math.Max(0m, GetCsvDec("MinWholesaleQuantity", "Min Wholesale Quantity", "CantMinMayorista"));
            dto.HasWholesale = GetCsvBool("HasWholesale", "Enable Wholesale", "HabilitarMayorista");

            dto.IsFractional = GetCsvBool("IsFractional", "Is Fractional", "EsFraccionable");
            dto.StockQuantity = Math.Max(0m, GetCsvDec("StockQuantity", "Current Stock", "StockActual", "Stock"));
            dto.LowStockThreshold = Math.Max(0m, GetCsvDec("LowStockThreshold", "Low Stock Threshold", "UmbralMinimo"));
            dto.UnitOfMeasure = dto.IsFractional ? "Kg" : "Und";
            dto.IsActive = true;

            dto.PriceRetailUSD = dto.CostPriceUSD > 0 && dto.ProfitMarginRetail > 0
                ? Math.Ceiling(dto.CostPriceUSD * (1m + dto.ProfitMarginRetail / 100m) * 100m) / 100m
                : 0m;

            if (dto.HasWholesale)
            {
                dto.PriceWholesaleUSD = dto.CostPriceUSD > 0 && dto.ProfitMarginWholesale > 0
                    ? Math.Ceiling(dto.CostPriceUSD * (1m + dto.ProfitMarginWholesale / 100m) * 100m) / 100m
                    : dto.PriceRetailUSD;
            }
            else
            {
                dto.PriceWholesaleUSD = dto.PriceRetailUSD;
                dto.ProfitMarginWholesale = dto.ProfitMarginRetail;
                dto.MinWholesaleQuantity = 0.000m;
            }

            if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("El nombre no puede estar vacío.");
            if (string.IsNullOrWhiteSpace(dto.SKU))
            {
                errors.Add("El SKU/Código de barras no puede estar vacío.");
            }
            else if (!System.Text.RegularExpressions.Regex.IsMatch(dto.SKU.Trim(), @"^\d+$"))
            {
                errors.Add("El SKU/Código de barras debe ser estrictamente un número entero (solo dígitos 0-9).");
            }

            if (errors.Any())
            {
                dto.IsValid = false;
                dto.ErrorMessage = string.Join(" | ", errors);
            }
            else
            {
                dto.IsValid = true;
                dto.ErrorMessage = string.Empty;
            }

            results.Add(dto);
        }

        return results;
    }

    public static bool TryNormalizeUnitOfMeasure(string input, out string canonicalUnit, out Core.Entities.UnitOfMeasureType enumVal)
    {
        canonicalUnit = "Und";
        enumVal = Core.Entities.UnitOfMeasureType.Und;

        if (string.IsNullOrWhiteSpace(input)) return true;

        var clean = input.Trim();

        if (string.Equals(clean, "Und", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Unidad", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Unidades", StringComparison.OrdinalIgnoreCase))
        {
            canonicalUnit = "Und";
            enumVal = Core.Entities.UnitOfMeasureType.Und;
            return true;
        }

        if (string.Equals(clean, "Kg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Kilo", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Kilogramo", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Kilogramos", StringComparison.OrdinalIgnoreCase))
        {
            canonicalUnit = "Kg";
            enumVal = Core.Entities.UnitOfMeasureType.Kg;
            return true;
        }

        if (string.Equals(clean, "Grs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Gramo", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Gramos", StringComparison.OrdinalIgnoreCase))
        {
            canonicalUnit = "Grs";
            enumVal = Core.Entities.UnitOfMeasureType.Grs;
            return true;
        }

        if (string.Equals(clean, "Lb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Libra", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Libras", StringComparison.OrdinalIgnoreCase))
        {
            canonicalUnit = "Lb";
            enumVal = Core.Entities.UnitOfMeasureType.Lb;
            return true;
        }

        if (string.Equals(clean, "Oz", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Onza", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Onzas", StringComparison.OrdinalIgnoreCase))
        {
            canonicalUnit = "Oz";
            enumVal = Core.Entities.UnitOfMeasureType.Oz;
            return true;
        }

        if (string.Equals(clean, "Lt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Lts", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Litro", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Litros", StringComparison.OrdinalIgnoreCase))
        {
            canonicalUnit = "Lt";
            enumVal = Core.Entities.UnitOfMeasureType.Lt;
            return true;
        }

        if (string.Equals(clean, "Ml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Mililitro", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "Mililitros", StringComparison.OrdinalIgnoreCase))
        {
            canonicalUnit = "Ml";
            enumVal = Core.Entities.UnitOfMeasureType.Ml;
            return true;
        }

        return false;
    }

    private string GetStringValue(IXLRow row, Dictionary<string, int> map, params string[] props)
    {
        foreach (var prop in props)
        {
            if (map.TryGetValue(prop, out int col))
            {
                var str = row.Cell(col).GetString()?.Trim();
                if (!string.IsNullOrEmpty(str)) return str;
            }
        }
        return string.Empty;
    }

    private bool TryParseDecimal(IXLRow row, Dictionary<string, int> map, out decimal result, params string[] props)
    {
        result = 0m;
        foreach (var prop in props)
        {
            if (map.TryGetValue(prop, out int col))
            {
                var cell = row.Cell(col);
                if (cell.TryGetValue<decimal>(out result)) return true;
                if (decimal.TryParse(cell.GetString()?.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out result)) return true;
            }
        }
        return false;
    }

    private bool TryParseInt(IXLRow row, Dictionary<string, int> map, out int result, params string[] props)
    {
        result = 0;
        foreach (var prop in props)
        {
            if (map.TryGetValue(prop, out int col))
            {
                var cell = row.Cell(col);
                if (cell.TryGetValue<int>(out result)) return true;
                if (decimal.TryParse(cell.GetString()?.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal decVal))
                {
                    result = Convert.ToInt32(Math.Round(decVal));
                    return true;
                }
            }
        }
        return false;
    }

    private bool ParseBoolValue(IXLRow row, Dictionary<string, int> map, params string[] props)
    {
        foreach (var prop in props)
        {
            if (map.TryGetValue(prop, out int col))
            {
                var cell = row.Cell(col);
                if (cell.TryGetValue<bool>(out bool bVal)) return bVal;
                var strVal = cell.GetString()?.Trim().ToLower();
                if (strVal == "true" || strVal == "yes" || strVal == "si" || strVal == "sí" || strVal == "1") return true;
            }
        }
        return false;
    }

    public async Task<(int added, int updated)> CommitImportAsync(IEnumerable<ProductImportDto> products, bool overwriteMerge)
    {
        var request = new BulkImportRequestDto
        {
            Products = products.ToList(),
            OverwriteMerge = overwriteMerge
        };

        var response = await _httpClient.PostAsJsonAsync("api/products/bulk-import", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        if (result != null)
        {
            int added = result["added"]?.GetValue<int>() ?? 0;
            int updated = result["updated"]?.GetValue<int>() ?? 0;
            return (added, updated);
        }

        return (0, 0);
    }

    public async Task<string> ExportProductsToFileAsync(string destinationPath, bool activeOnly = true, string? filter = null)
    {
        var format = destinationPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? "xlsx" : "csv";
        var query = $"api/products/export?format={format}&activeOnly={activeOnly}";
        if (!string.IsNullOrWhiteSpace(filter))
        {
            query += $"&filter={Uri.EscapeDataString(filter)}";
        }

        var bytes = await _httpClient.GetByteArrayAsync(query);
        await File.WriteAllBytesAsync(destinationPath, bytes);
        return destinationPath;
    }
}
