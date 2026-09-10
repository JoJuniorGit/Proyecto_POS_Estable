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

public partial class ProductImportService : IProductImportService
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
