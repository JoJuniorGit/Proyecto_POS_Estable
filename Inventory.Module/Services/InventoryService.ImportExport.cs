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
    // 8I-M3: tope de lote del import (evita cargas de memoria/CPU ilimitadas desde el cliente).
    private const int MaxImportBatchSize = 5000;
    // 8I-M3: SKU con la misma regla del catálogo (ProductsController regex ^[A-Za-z0-9\-_]{1,50}$).
    private static readonly System.Text.RegularExpressions.Regex SkuRegex =
        new("^[A-Za-z0-9\\-_]{1,50}$", System.Text.RegularExpressions.RegexOptions.Compiled);


    /// <summary>
    /// 8I-M3: validación de importación EN EL SERVIDOR (no confía en dto.IsValid calculado en el
    /// cliente): SKU con formato válido, nombre no vacío y montos/cantidades no negativos.
    /// </summary>
    private static bool IsImportable(Core.DTOs.ProductImportDto p)
    {
        if (string.IsNullOrWhiteSpace(p.SKU) || !SkuRegex.IsMatch(p.SKU.Trim())) return false;
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Trim().Length > 200) return false;

        bool NonNeg(decimal v) => v >= 0;
        return NonNeg(p.CostPriceUSD)
            && NonNeg(p.PriceRetailUSD)
            && NonNeg(p.PriceWholesaleUSD)
            && NonNeg(p.MinWholesaleQuantity)
            && NonNeg(p.StockQuantity)
            && NonNeg(p.LowStockThreshold);
    }

    public async Task<(int added, int updated)> BulkImportProductsAsync(IEnumerable<Core.DTOs.ProductImportDto> products, bool overwriteMerge, CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        int added = 0;
        int updated = 0;

        if (products == null)
        {
            throw new ArgumentException("La lista de productos a importar no puede ser nula.", nameof(products));
        }

        var productList = products.ToList();
        if (productList.Count == 0)
        {
            return (0, 0);
        }

        if (productList.Count > MaxImportBatchSize)
        {
            throw new ArgumentException($"El lote de importación excede el máximo permitido ({MaxImportBatchSize} productos).");
        }

        // 8.16-H02: la transacción manual debe vivir DENTRO de CreateExecutionStrategy().ExecuteAsync()
        // para no lanzar InvalidOperationException bajo NpgsqlRetryingExecutionStrategy en producción.
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            using var transaction = _context.Database.IsRelational() ? await _context.Database.BeginTransactionAsync(cancellationToken) : null;
            try
            {
            var skusToImport = productList.Where(IsImportable).Select(p => p.SKU.Trim()).Distinct().ToList();
            var existingProducts = await _context.Products
                .Where(p => skusToImport.Contains(p.SKU))
                .ToDictionaryAsync(p => p.SKU, cancellationToken);

            // First pass: Process groups and normal products to ensure parent IDs exist for variants
            var groupDictionary = await _context.Products
                .Where(p => p.IsGroupHeader && !p.IsDeleted)
                .ToDictionaryAsync(p => p.GroupKey ?? p.Name, p => p, StringComparer.OrdinalIgnoreCase, cancellationToken);

            foreach (var dto in productList)
            {
                if (!IsImportable(dto)) continue; // 8I-M3: validación en servidor, no dto.IsValid

                var isGroup = string.Equals(dto.ProductType, "Grupo", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(dto.ProductType, "Group", StringComparison.OrdinalIgnoreCase);

                var isVariant = string.Equals(dto.ProductType, "Variante", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(dto.ProductType, "Variant", StringComparison.OrdinalIgnoreCase);

                var (cost, marginRetail, priceRetail, marginWholesale, priceWholesale, minWholesaleQty, hasWholesale) = ResolveImportPricing(dto);
                var (unitEnum, isFractional) = ResolveUnitOfMeasure(dto.UnitOfMeasure, dto.IsFractional);

                var skuClean = dto.SKU.Trim();
                var groupKey = !string.IsNullOrWhiteSpace(dto.GroupNameOrKey) ? dto.GroupNameOrKey.Trim() : (isGroup ? dto.Name.Trim() : null);

                if (existingProducts.TryGetValue(skuClean, out var existingProduct))
                {
                    if (overwriteMerge)
                    {
                        existingProduct.Name = dto.Name.Trim();
                        existingProduct.Description = dto.Description?.Trim() ?? string.Empty;
                        existingProduct.IsGroupHeader = isGroup;
                        existingProduct.GroupKey = groupKey;

                        if (isGroup)
                        {
                            existingProduct.CostPriceUSD = cost;
                            existingProduct.Cost = cost;
                            existingProduct.ProfitMarginRetail = marginRetail;
                            existingProduct.ProfitPercentage = marginRetail;
                            existingProduct.PriceRetailUSD = priceRetail;
                            existingProduct.PriceUSD = priceRetail;
                            existingProduct.ProfitMarginWholesale = marginWholesale;
                            existingProduct.PriceWholesaleUSD = priceWholesale;
                            existingProduct.MinWholesaleQuantity = minWholesaleQty;
                            existingProduct.HasWholesale = hasWholesale;
                            existingProduct.IsFractional = isFractional;
                            existingProduct.UnitOfMeasure = unitEnum;
                            existingProduct.ParentProductId = null;
                            if (existingProduct.IsStockShared)
                            {
                                existingProduct.StockQuantity = Math.Max(0, dto.StockQuantity);
                                existingProduct.LowStockThreshold = Math.Max(0, dto.LowStockThreshold);
                            }
                            else
                            {
                                existingProduct.StockQuantity = 0m;
                                existingProduct.LowStockThreshold = 0m;
                            }
                            if (groupKey != null) groupDictionary[groupKey] = existingProduct;
                        }
                        else
                        {
                            existingProduct.IsStockShared = false;
                            existingProduct.HasIndependentPricing = false;

                            if (isVariant && !string.IsNullOrWhiteSpace(dto.GroupNameOrKey) && groupDictionary.TryGetValue(dto.GroupNameOrKey.Trim(), out var parentGrp))
                            {
                                existingProduct.ParentProductId = parentGrp.Id > 0 ? parentGrp.Id : (int?)null;
                                if (!parentGrp.HasIndependentPricing)
                                {
                                    existingProduct.CostPriceUSD = parentGrp.CostPriceUSD;
                                    existingProduct.Cost = parentGrp.CostPriceUSD;
                                    existingProduct.ProfitMarginRetail = parentGrp.ProfitMarginRetail;
                                    existingProduct.ProfitPercentage = parentGrp.ProfitMarginRetail;
                                    existingProduct.PriceRetailUSD = parentGrp.PriceRetailUSD;
                                    existingProduct.PriceUSD = parentGrp.PriceRetailUSD;
                                    existingProduct.ProfitMarginWholesale = parentGrp.ProfitMarginWholesale;
                                    existingProduct.PriceWholesaleUSD = parentGrp.PriceWholesaleUSD;
                                    existingProduct.MinWholesaleQuantity = parentGrp.MinWholesaleQuantity;
                                    existingProduct.HasWholesale = parentGrp.HasWholesale;
                                    existingProduct.IsFractional = parentGrp.IsFractional;
                                    existingProduct.UnitOfMeasure = parentGrp.UnitOfMeasure;
                                }
                                else
                                {
                                    existingProduct.CostPriceUSD = cost;
                                    existingProduct.Cost = cost;
                                    existingProduct.ProfitMarginRetail = marginRetail;
                                    existingProduct.ProfitPercentage = marginRetail;
                                    existingProduct.PriceRetailUSD = priceRetail;
                                    existingProduct.PriceUSD = priceRetail;
                                    existingProduct.ProfitMarginWholesale = marginWholesale;
                                    existingProduct.PriceWholesaleUSD = priceWholesale;
                                    existingProduct.MinWholesaleQuantity = minWholesaleQty;
                                    existingProduct.HasWholesale = hasWholesale;
                                    existingProduct.IsFractional = isFractional;
                                    existingProduct.UnitOfMeasure = unitEnum;
                                }

                                if (parentGrp.IsStockShared)
                                {
                                    existingProduct.StockQuantity = 0m;
                                    existingProduct.LowStockThreshold = 0m;
                                    existingProduct.ConversionFactor = dto.ConversionFactor > 0 ? dto.ConversionFactor : 1.0000m;
                                }
                                else
                                {
                                    existingProduct.StockQuantity = Math.Max(0, dto.StockQuantity);
                                    existingProduct.LowStockThreshold = Math.Max(0, dto.LowStockThreshold);
                                    existingProduct.ConversionFactor = 1.0000m;
                                }
                            }
                            else
                            {
                                existingProduct.ConversionFactor = 1.0000m;
                                existingProduct.CostPriceUSD = cost;
                                existingProduct.Cost = cost;
                                existingProduct.ProfitMarginRetail = marginRetail;
                                existingProduct.ProfitPercentage = marginRetail;
                                existingProduct.PriceRetailUSD = priceRetail;
                                existingProduct.PriceUSD = priceRetail;
                                existingProduct.ProfitMarginWholesale = marginWholesale;
                                existingProduct.PriceWholesaleUSD = priceWholesale;
                                existingProduct.MinWholesaleQuantity = minWholesaleQty;
                                existingProduct.HasWholesale = hasWholesale;
                                existingProduct.IsFractional = isFractional;
                                existingProduct.UnitOfMeasure = unitEnum;
                                existingProduct.StockQuantity = Math.Max(0, dto.StockQuantity);
                                existingProduct.LowStockThreshold = Math.Max(0, dto.LowStockThreshold);
                            }
                        }

                        existingProduct.UpdatedAt = DateTime.UtcNow;
                        updated++;
                    }
                }
                else
                {
                    var newProduct = new Product
                    {
                        SKU = skuClean,
                        Name = dto.Name.Trim(),
                        Description = dto.Description?.Trim() ?? string.Empty,
                        IsGroupHeader = isGroup,
                        GroupKey = groupKey,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };

                    if (isGroup)
                    {
                        newProduct.IsStockShared = dto.IsStockShared;
                        newProduct.HasIndependentPricing = dto.HasIndependentPricing;
                        newProduct.CostPriceUSD = cost;
                        newProduct.Cost = cost;
                        newProduct.ProfitMarginRetail = marginRetail;
                        newProduct.ProfitPercentage = marginRetail;
                        newProduct.PriceRetailUSD = priceRetail;
                        newProduct.PriceUSD = priceRetail;
                        newProduct.ProfitMarginWholesale = marginWholesale;
                        newProduct.PriceWholesaleUSD = priceWholesale;
                        newProduct.MinWholesaleQuantity = minWholesaleQty;
                        newProduct.HasWholesale = hasWholesale;
                        newProduct.IsFractional = isFractional;
                        newProduct.UnitOfMeasure = unitEnum;
                        newProduct.ParentProductId = null;
                        if (dto.IsStockShared)
                        {
                            newProduct.StockQuantity = Math.Max(0, dto.StockQuantity);
                            newProduct.LowStockThreshold = Math.Max(0, dto.LowStockThreshold);
                        }
                        else
                        {
                            newProduct.StockQuantity = 0m;
                            newProduct.LowStockThreshold = 0m;
                        }
                        if (groupKey != null) groupDictionary[groupKey] = newProduct;
                    }
                    else
                    {
                        newProduct.IsStockShared = false;
                        newProduct.HasIndependentPricing = false;

                        if (isVariant && !string.IsNullOrWhiteSpace(dto.GroupNameOrKey) && groupDictionary.TryGetValue(dto.GroupNameOrKey.Trim(), out var parentGrp))
                        {
                            newProduct.ParentProduct = parentGrp;
                            if (!parentGrp.HasIndependentPricing)
                            {
                                newProduct.CostPriceUSD = parentGrp.CostPriceUSD;
                                newProduct.Cost = parentGrp.CostPriceUSD;
                                newProduct.ProfitMarginRetail = parentGrp.ProfitMarginRetail;
                                newProduct.ProfitPercentage = parentGrp.ProfitMarginRetail;
                                newProduct.PriceRetailUSD = parentGrp.PriceRetailUSD;
                                newProduct.PriceUSD = parentGrp.PriceRetailUSD;
                                newProduct.ProfitMarginWholesale = parentGrp.ProfitMarginWholesale;
                                newProduct.PriceWholesaleUSD = parentGrp.PriceWholesaleUSD;
                                newProduct.MinWholesaleQuantity = parentGrp.MinWholesaleQuantity;
                                newProduct.HasWholesale = parentGrp.HasWholesale;
                                newProduct.IsFractional = parentGrp.IsFractional;
                                newProduct.UnitOfMeasure = parentGrp.UnitOfMeasure;
                            }
                            else
                            {
                                newProduct.CostPriceUSD = cost;
                                newProduct.Cost = cost;
                                newProduct.ProfitMarginRetail = marginRetail;
                                newProduct.ProfitPercentage = marginRetail;
                                newProduct.PriceRetailUSD = priceRetail;
                                newProduct.PriceUSD = priceRetail;
                                newProduct.ProfitMarginWholesale = marginWholesale;
                                newProduct.PriceWholesaleUSD = priceWholesale;
                                newProduct.MinWholesaleQuantity = minWholesaleQty;
                                newProduct.HasWholesale = hasWholesale;
                                newProduct.IsFractional = isFractional;
                                newProduct.UnitOfMeasure = unitEnum;
                            }

                            if (parentGrp.IsStockShared)
                            {
                                newProduct.StockQuantity = 0m;
                                newProduct.LowStockThreshold = 0m;
                                newProduct.ConversionFactor = dto.ConversionFactor > 0 ? dto.ConversionFactor : 1.0000m;
                            }
                            else
                            {
                                newProduct.StockQuantity = Math.Max(0, dto.StockQuantity);
                                newProduct.LowStockThreshold = Math.Max(0, dto.LowStockThreshold);
                                newProduct.ConversionFactor = 1.0000m;
                            }
                        }
                        else
                        {
                            newProduct.ConversionFactor = 1.0000m;
                            newProduct.CostPriceUSD = cost;
                            newProduct.Cost = cost;
                            newProduct.ProfitMarginRetail = marginRetail;
                            newProduct.ProfitPercentage = marginRetail;
                            newProduct.PriceRetailUSD = priceRetail;
                            newProduct.PriceUSD = priceRetail;
                            newProduct.ProfitMarginWholesale = marginWholesale;
                            newProduct.PriceWholesaleUSD = priceWholesale;
                            newProduct.MinWholesaleQuantity = minWholesaleQty;
                            newProduct.HasWholesale = hasWholesale;
                            newProduct.IsFractional = isFractional;
                            newProduct.UnitOfMeasure = unitEnum;
                            newProduct.StockQuantity = Math.Max(0, dto.StockQuantity);
                            newProduct.LowStockThreshold = Math.Max(0, dto.LowStockThreshold);
                        }
                    }

                    _context.Products.Add(newProduct);
                    existingProducts[skuClean] = newProduct;
                    added++;
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            InvalidateAllProductCaches();
            return (added, updated);
            }
            catch (Exception)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                throw;
            }
        });
    }

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

        bool isXlsx = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "excel", StringComparison.OrdinalIgnoreCase);

        if (isXlsx)
        {
            using var workbook = new ClosedXML.Excel.XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Productos");

            var headers = new[]
            {
                "SKU", "Nombre", "Descripción", "CostoUSD", "MargenDetal%",
                "MargenMayor%", "CantMinMayorista", "HabilitarMayorista",
                "EsFraccionable", "StockActual", "UmbralMinimo"
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
            csvBuilder.AppendLine("SKU;Nombre;Descripción;CostoUSD;MargenDetal%;MargenMayor%;CantMinMayorista;HabilitarMayorista;EsFraccionable;StockActual;UmbralMinimo");

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
                    p.LowStockThreshold.ToString()
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
                "EsFraccionable", "StockActual", "UmbralMinimo"
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

            worksheet.Columns().AdjustToContents();

            using var memoryStream = new MemoryStream();
            workbook.SaveAs(memoryStream);
            return Task.FromResult(memoryStream.ToArray());
        }
        else
        {
            var csvBuilder = new StringBuilder();
            csvBuilder.AppendLine("SKU;Nombre;Descripción;CostoUSD;MargenDetal%;MargenMayor%;CantMinMayorista;HabilitarMayorista;EsFraccionable;StockActual;UmbralMinimo");
            csvBuilder.AppendLine("1001;Producto Ejemplo;Descripción breve de ejemplo;10.00;30.00;20.00;6.000;SI;NO;100;5");

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
}
