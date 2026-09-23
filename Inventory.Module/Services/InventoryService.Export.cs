using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public partial class InventoryService
{

    public async Task<byte[]> ExportProductsAsync(string format, bool activeOnly, string? filter = null, CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        var query = _context.Products.AsNoTracking();

        if (activeOnly)
        {
            query = query.Where(p => p.IsActive && !p.IsDeleted);
        }
        else
        {
            query = query.Where(p => !p.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var cleanFilter = filter.Trim().ToLower();
            query = query.Where(p => p.SKU.ToLower().Contains(cleanFilter) || p.Name.ToLower().Contains(cleanFilter));
        }

        var products = await query.OrderBy(p => p.SKU).ToListAsync(cancellationToken);

        var groupKeysById = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsGroupHeader && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id, p => !string.IsNullOrWhiteSpace(p.GroupKey) ? p.GroupKey : p.Name, cancellationToken);

        bool isXlsx = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "excel", StringComparison.OrdinalIgnoreCase);

        if (isXlsx)
        {
            using var workbook = new ClosedXML.Excel.XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Productos");

            var headers = new[]
            {
                "SKU", "Nombre", "Descripción", "CostoUSD", "MargenDetal%",
                "MargenMayor%", "CantMinMayorista", "HabilitarMayorista",
                "EsFraccionable", "StockActual", "UmbralMinimo",
                "TipoProducto", "Grupo", "CompartirStock", "FactorConversion"
            };

            for (int col = 0; col < headers.Length; col++)
            {
                var cell = worksheet.Cell(1, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#1E88E5");
                cell.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
            }

            int row = 2;
            foreach (var p in products)
            {
                worksheet.Cell(row, 1).Value = p.SKU;
                worksheet.Cell(row, 2).Value = p.Name;
                worksheet.Cell(row, 3).Value = p.Description;
                worksheet.Cell(row, 4).Value = p.CostPriceUSD;
                worksheet.Cell(row, 5).Value = p.ProfitMarginRetail;
                worksheet.Cell(row, 6).Value = p.ProfitMarginWholesale;
                worksheet.Cell(row, 7).Value = p.MinWholesaleQuantity;
                worksheet.Cell(row, 8).Value = p.HasWholesale ? "SI" : "NO";
                worksheet.Cell(row, 9).Value = p.IsFractional ? "SI" : "NO";
                worksheet.Cell(row, 10).Value = p.StockQuantity;
                worksheet.Cell(row, 11).Value = p.LowStockThreshold;
                worksheet.Cell(row, 12).Value = ResolveProductType(p);
                worksheet.Cell(row, 13).Value = ResolveGroupName(p, groupKeysById);
                worksheet.Cell(row, 14).Value = p.IsStockShared ? "SI" : "NO";
                worksheet.Cell(row, 15).Value = p.ConversionFactor;
                row++;
            }

            worksheet.Columns().AdjustToContents();

            using var memoryStream = new MemoryStream();
            workbook.SaveAs(memoryStream);
            return memoryStream.ToArray();
        }
        else
        {
            var csvBuilder = new StringBuilder();
            csvBuilder.AppendLine("SKU;Nombre;Descripción;CostoUSD;MargenDetal%;MargenMayor%;CantMinMayorista;HabilitarMayorista;EsFraccionable;StockActual;UmbralMinimo;TipoProducto;Grupo;CompartirStock;FactorConversion");

            foreach (var p in products)
            {
                csvBuilder.AppendLine(string.Join(";", new string[]
                {
                    EscapeCsvField(p.SKU),
                    EscapeCsvField(p.Name),
                    EscapeCsvField(p.Description),
                    p.CostPriceUSD.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    p.ProfitMarginRetail.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    p.ProfitMarginWholesale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    p.MinWholesaleQuantity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                    p.HasWholesale ? "SI" : "NO",
                    p.IsFractional ? "SI" : "NO",
                    p.StockQuantity.ToString(),
                    p.LowStockThreshold.ToString(),
                    ResolveProductType(p),
                    EscapeCsvField(ResolveGroupName(p, groupKeysById)),
                    p.IsStockShared ? "SI" : "NO",
                    p.ConversionFactor.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                }));
            }

            return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csvBuilder.ToString())).ToArray();
        }
    }

    public Task<byte[]> GenerateTemplateAsync(string format, CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        bool isXlsx = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "excel", StringComparison.OrdinalIgnoreCase);

        if (isXlsx)
        {
            using var workbook = new ClosedXML.Excel.XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Plantilla_Productos");

            var headers = new[]
            {
                "SKU", "Nombre", "Descripción", "CostoUSD", "MargenDetal%",
                "MargenMayor%", "CantMinMayorista", "HabilitarMayorista",
                "EsFraccionable", "StockActual", "UmbralMinimo",
                "TipoProducto", "Grupo", "CompartirStock", "FactorConversion"
            };

            for (int col = 0; col < headers.Length; col++)
            {
                var cell = worksheet.Cell(1, col + 1);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#1E88E5");
                cell.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
            }

            // Sample Row
            worksheet.Cell(2, 1).Value = "1001";
            worksheet.Cell(2, 2).Value = "Producto Ejemplo";
            worksheet.Cell(2, 3).Value = "Descripción breve de ejemplo";
            worksheet.Cell(2, 4).Value = 10.00m;
            worksheet.Cell(2, 5).Value = 30.00m;
            worksheet.Cell(2, 6).Value = 20.00m;
            worksheet.Cell(2, 7).Value = 6.000m;
            worksheet.Cell(2, 8).Value = "SI";
            worksheet.Cell(2, 9).Value = "NO";
            worksheet.Cell(2, 10).Value = 100;
            worksheet.Cell(2, 11).Value = 5;
            worksheet.Cell(2, 12).Value = "Normal";
            worksheet.Cell(2, 13).Value = "";
            worksheet.Cell(2, 14).Value = "NO";
            worksheet.Cell(2, 15).Value = 1.0000m;

            worksheet.Columns().AdjustToContents();

            using var memoryStream = new MemoryStream();
            workbook.SaveAs(memoryStream);
            return Task.FromResult(memoryStream.ToArray());
        }
        else
        {
            var csvBuilder = new StringBuilder();
            csvBuilder.AppendLine("SKU;Nombre;Descripción;CostoUSD;MargenDetal%;MargenMayor%;CantMinMayorista;HabilitarMayorista;EsFraccionable;StockActual;UmbralMinimo;TipoProducto;Grupo;CompartirStock;FactorConversion");
            csvBuilder.AppendLine("1001;Producto Ejemplo;Descripción breve de ejemplo;10.00;30.00;20.00;6.000;SI;NO;100;5;Normal;;NO;1.0000");

            return Task.FromResult(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csvBuilder.ToString())).ToArray());
        }
    }

    private static (decimal cost, decimal marginRetail, decimal priceRetail, decimal marginWholesale, decimal priceWholesale, decimal minWholesaleQty, bool hasWholesale) ResolveImportPricing(Core.DTOs.ProductImportDto dto)
    {
        decimal cost = Math.Max(0m, dto.CostPriceUSD);
        decimal marginRetail = Math.Max(0m, dto.ProfitMarginRetail);
        decimal priceRetail = cost > 0 && marginRetail > 0 ? Core.Helpers.PricingCalculator.RoundPriceUp(cost * (1m + marginRetail / 100m)) : dto.PriceRetailUSD;

        bool hasWholesale = dto.HasWholesale;
        decimal marginWholesale = 0m;
        decimal priceWholesale = 0m;
        decimal minWholesaleQty = 0m;

        if (hasWholesale)
        {
            marginWholesale = Math.Max(0m, dto.ProfitMarginWholesale);
            priceWholesale = cost > 0 && marginWholesale > 0 ? Core.Helpers.PricingCalculator.RoundPriceUp(cost * (1m + marginWholesale / 100m)) : dto.PriceWholesaleUSD;

            if (priceWholesale > priceRetail)
            {
                priceWholesale = priceRetail;
                marginWholesale = marginRetail;
            }
            minWholesaleQty = Math.Max(0m, dto.MinWholesaleQuantity);
        }
        else
        {
            priceWholesale = priceRetail;
            marginWholesale = marginRetail;
            minWholesaleQty = 0m;
        }

        return (cost, marginRetail, priceRetail, marginWholesale, priceWholesale, minWholesaleQty, hasWholesale);
    }

    private static (UnitOfMeasureType unit, bool isFractional) ResolveUnitOfMeasure(string unitStr, bool isFractionalInput)
    {
        // 8I-M10: el texto de la unidad importada se respeta ("Kg"/"kilos"/"g"/"und"...);
        // el flag booleano del cliente solo actúa como respaldo cuando el texto no es reconocible.
        var unit = (unitStr ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "kg" or "kilo" or "kilos" or "kilogramo" or "kilogramos" => UnitOfMeasureType.Kg,
            "g" or "gr" or "gramo" or "gramos" => UnitOfMeasureType.Grs,
            "lb" or "libra" or "libras" => UnitOfMeasureType.Lb,
            "oz" or "onza" or "onzas" => UnitOfMeasureType.Oz,
            "l" or "lt" or "litro" or "litros" => UnitOfMeasureType.Lt,
            "ml" or "mililitro" or "mililitros" => UnitOfMeasureType.Ml,
            "und" or "unidad" or "unidades" or "pza" or "pzas" or "pieza" or "piezas" => UnitOfMeasureType.Und,
            _ when isFractionalInput => UnitOfMeasureType.Kg,
            _ => UnitOfMeasureType.Und
        };

        bool isFractional = isFractionalInput
            || unit is UnitOfMeasureType.Kg or UnitOfMeasureType.Grs or UnitOfMeasureType.Lb
                or UnitOfMeasureType.Oz or UnitOfMeasureType.Lt or UnitOfMeasureType.Ml;
        return (unit, isFractional);
    }

    private static string EscapeCsvField(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";

        // 8I-M8: neutralizar también NUL y espacios iniciales (Excel los interpreta como
        // fórmula precedida de espacio; \0 es un separador de celda legacy).
        field = field.Replace("\0", "");
        var trimmedStart = field.TrimStart();
        bool hadLeadingSpace = trimmedStart.Length != field.Length;

        // Neutralize CSV/Excel Formula Injection (CWE-1236)
        if (hadLeadingSpace
            || field.StartsWith('=') || field.StartsWith('+')
            || field.StartsWith('-') || field.StartsWith('@')
            || field.StartsWith('\t') || field.StartsWith('\r'))
        {
            field = "'" + trimmedStart;
        }

        if (field.Contains(";") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }

    private static string ResolveProductType(Core.Entities.Product p)
    {
        if (p.IsGroupHeader) return "Grupo";
        if (p.ParentProductId.HasValue) return "Variante";
        return "Normal";
    }

    private static string ResolveGroupName(Core.Entities.Product p, System.Collections.Generic.Dictionary<int, string> groupKeysById)
    {
        if (p.IsGroupHeader)
        {
            return !string.IsNullOrWhiteSpace(p.GroupKey) ? p.GroupKey : p.Name;
        }
        if (p.ParentProductId.HasValue && groupKeysById.TryGetValue(p.ParentProductId.Value, out var groupName))
        {
            return groupName;
        }
        return string.Empty;
    }
}
