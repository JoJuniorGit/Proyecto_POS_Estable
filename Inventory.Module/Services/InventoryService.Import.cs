using System;
using System.Collections.Generic;
using System.Linq;
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
                                existingProduct.StockQuantity = existingProduct.StockQuantity + Math.Max(0, dto.StockQuantity);
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
                                if (parentGrp.Id > 0)
                                {
                                    existingProduct.ParentProductId = parentGrp.Id;
                                }
                                else
                                {
                                    existingProduct.ParentProduct = parentGrp;
                                }
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
                                    existingProduct.StockQuantity = existingProduct.StockQuantity + Math.Max(0, dto.StockQuantity);
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
                                existingProduct.StockQuantity = existingProduct.StockQuantity + Math.Max(0, dto.StockQuantity);
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

}
