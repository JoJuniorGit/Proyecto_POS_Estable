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

            // AUD-23: identificador de lote compartido por todos los movimientos de esta llamada.
            var importReason = $"Importación masiva IMP-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

            // 8.179-T1 (S5): índice de categorías por ruta normalizada ("A>B") para resolver o
            // auto-crear la jerarquía del archivo dentro de la misma transacción.
            var categoriesByPath = await BuildCategoryIndexByPathAsync(cancellationToken);

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
                        decimal stockBefore = existingProduct.StockQuantity;
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

                        // AUD-23: los forzados a 0 por regla (grupo sin stock compartido / variante de
                        // padre compartido) no emiten movimiento — silencio consistente con AUD-21.
                        // La referencia va por navegación (no ProductId): el "existente" puede ser un alta
                        // del mismo lote con Id aún sin asignar; un FK explícito 0 falla en relacional.
                        decimal stockDelta = existingProduct.StockQuantity - stockBefore;
                        if (stockDelta > 0)
                        {
                            _context.StockMovements.Add(new StockMovement
                            {
                                Product = existingProduct,
                                QuantityChange = stockDelta,
                                NewStockLevel = existingProduct.StockQuantity,
                                Reason = importReason,
                                SaleId = null,
                                MovementDate = DateTime.UtcNow,
                                UserId = _currentUserService?.UserId,
                                // 8.177-T1: la importación masiva carga/ajusta stock desde archivo
                                // sin factura proveedor asociada; se clasifica Importación + Ajuste
                                // de importación (no Entrada por Compra: esa exige referencia de
                                // factura y se emite en SupplierInvoiceService.Apply).
                                OriginType = StockMovementOriginType.Import,
                                ReasonCode = StockMovementReason.ImportAdjustment,
                                UnitCostUSD = existingProduct.CostPriceUSD
                            });
                        }

                        existingProduct.UpdatedAt = DateTime.UtcNow;
                        await ApplyImportedCategoryAsync(dto, existingProduct, categoriesByPath, cancellationToken);
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

                    if (newProduct.StockQuantity > 0)
                    {
                        _context.StockMovements.Add(new StockMovement
                        {
                            Product = newProduct,
                            QuantityChange = newProduct.StockQuantity,
                            NewStockLevel = newProduct.StockQuantity,
                            Reason = importReason,
                            SaleId = null,
                            MovementDate = DateTime.UtcNow,
                            UserId = _currentUserService?.UserId,
                            // 8.177-T1: mismo criterio que el merge de importación (ver arriba):
                            // origen Importación + ajuste por archivo, sin referencia de factura.
                            OriginType = StockMovementOriginType.Import,
                            ReasonCode = StockMovementReason.ImportAdjustment,
                            UnitCostUSD = newProduct.CostPriceUSD
                        });
                    }

                    _context.Products.Add(newProduct);
                    existingProducts[skuClean] = newProduct;
                    await ApplyImportedCategoryAsync(dto, newProduct, categoriesByPath, cancellationToken);
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

    /// <summary>
    /// 8.179-T1 (S5): índice de categorías por ruta normalizada ("ALIMENTOS>LÁCTEOS") construido
    /// en memoria; las creadas por el lote se agregan al mismo índice (con navegación al padre aún
    /// sin Id, EF fija el FK al guardar).
    /// </summary>
    private async Task<Dictionary<string, Category>> BuildCategoryIndexByPathAsync(CancellationToken cancellationToken)
    {
        var categories = await _context.Categories.ToListAsync(cancellationToken);
        var paths = CategoryHierarchy.BuildPaths(categories, normalized: true);

        var index = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            index[paths[category.Id]] = category;
        }

        return index;
    }

    /// <summary>
    /// 8.179-T1 (S5): resuelve la ruta jerárquica separada por '>' contra el índice (case-insensitive
    /// vía NormalizedName) y auto-crea los segmentos faltantes de raíz a hoja. Devuelve la hoja.
    /// </summary>
    private Category? ResolveOrCreateCategoryPath(string? categoryPath, Dictionary<string, Category> categoriesByPath)
    {
        if (string.IsNullOrWhiteSpace(categoryPath))
        {
            return null;
        }

        var segments = categoryPath
            .Split('>', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToList();

        if (segments.Count == 0)
        {
            return null;
        }

        Category? parent = null;
        var path = string.Empty;
        foreach (var segment in segments)
        {
            var normalized = segment.ToUpperInvariant();
            path = path.Length == 0 ? normalized : path + ">" + normalized;

            if (!categoriesByPath.TryGetValue(path, out var category))
            {
                category = new Category
                {
                    Name = segment,
                    NormalizedName = normalized,
                    ParentCategory = parent
                };
                _context.Categories.Add(category);
                categoriesByPath[path] = category;
            }

            parent = category;
        }

        return parent;
    }

    /// <summary>
    /// 8.179-T1 (S5): asigna la categoría HOJA de la ruta al producto importado, reemplazando las
    /// asignaciones previas cuando el archivo trae categoría (celda vacía = no tocar).
    /// </summary>
    private async Task ApplyImportedCategoryAsync(Core.DTOs.ProductImportDto dto, Product product, Dictionary<string, Category> categoriesByPath, CancellationToken cancellationToken)
    {
        var leaf = ResolveOrCreateCategoryPath(dto.CategoryPath, categoriesByPath);
        if (leaf == null)
        {
            return;
        }

        if (product.Id != 0)
        {
            var previous = await _context.ProductCategories
                .Where(pc => pc.ProductId == product.Id)
                .ToListAsync(cancellationToken);
            _context.ProductCategories.RemoveRange(previous);
        }
        else
        {
            // Producto nuevo aún sin Id: purgar asignaciones pendientes del mismo lote por navegación.
            var pending = _context.ProductCategories.Local
                .Where(pc => ReferenceEquals(pc.Product, product))
                .ToList();
            _context.ProductCategories.RemoveRange(pending);
        }

        _context.ProductCategories.Add(new ProductCategory { Product = product, Category = leaf });
    }

}
