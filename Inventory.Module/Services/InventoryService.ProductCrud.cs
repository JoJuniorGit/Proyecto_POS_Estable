using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

public partial class InventoryService
{

    public async Task<Product> CreateProductAsync(Product product)
    {
        EnsureCatalogMutationPermission();

        if (product.IsCashAdvance)
        {
            if (product.IsGroupHeader)
            {
                throw new ArgumentException("Un producto configurado como Servicio de Adelanto de Efectivo no puede ser un grupo de variantes.");
            }
            if (product.ParentProductId.HasValue)
            {
                throw new ArgumentException("Un producto configurado como Servicio de Adelanto de Efectivo no puede ser variante de un producto padre.");
            }

            product.IsFractional = false;
            product.UnitOfMeasure = UnitOfMeasureType.Und;
            product.StockQuantity = 0m;
            product.ReservedQuantity = 0m;
            product.LowStockThreshold = 0m;
            product.IsStockShared = false;
            product.HasIndependentPricing = false;
            product.ConversionFactor = 1.0000m;
        }
        else if (product.IsGroupHeader)
        {
            product.ConversionFactor = 1.0000m;
            if (string.IsNullOrWhiteSpace(product.SKU))
            {
                product.SKU = $"GRP-{DateTime.UtcNow.Ticks}";
            }
            product.ParentProductId = null;
            product.ReservedQuantity = 0m;
            if (!product.IsStockShared)
            {
                product.StockQuantity = 0m;
                product.LowStockThreshold = 0m;
            }
            if (string.IsNullOrWhiteSpace(product.GroupKey))
            {
                product.GroupKey = product.Name.Trim();
            }
            if (product.HasIndependentPricing)
            {
                product.CostPriceUSD = 0m;
                product.Cost = 0m;
                product.ProfitMarginRetail = 0m;
                product.ProfitPercentage = 0m;
                product.PriceRetailUSD = 0m;
                product.PriceUSD = 0m;
                product.PriceBsS = 0m;
                product.HasWholesale = false;
                product.ProfitMarginWholesale = 0m;
                product.PriceWholesaleUSD = 0m;
            }
        }
        else if (product.ParentProductId.HasValue)
        {
            var parent = await _context.Products.FindAsync(product.ParentProductId.Value);
            if (parent == null || parent.IsDeleted)
            {
                throw new KeyNotFoundException($"Producto padre con ID {product.ParentProductId.Value} no encontrado.");
            }

            product.IsGroupHeader = false;
            product.IsStockShared = false;
            product.HasIndependentPricing = false;

            if (parent.IsStockShared)
            {
                product.StockQuantity = 0m;
                product.ReservedQuantity = 0m;
                product.LowStockThreshold = 0m;
                if (product.ConversionFactor < 0.0001m || product.ConversionFactor > 1_000_000m)
                {
                    throw new ArgumentException(Core.Constants.InventoryMessages.ConversionFactorOutOfRange);
                }
            }
            else
            {
                product.ConversionFactor = 1.0000m;
            }

            if (!parent.HasIndependentPricing)
            {
                // Inherit pricing and configuration from parent
                product.PriceRetailUSD = parent.PriceRetailUSD;
                product.PriceWholesaleUSD = parent.PriceWholesaleUSD;
                product.CostPriceUSD = parent.CostPriceUSD;
                product.ProfitMarginRetail = parent.ProfitMarginRetail;
                product.ProfitMarginWholesale = parent.ProfitMarginWholesale;
                product.HasWholesale = parent.HasWholesale;
                product.IsFractional = parent.IsFractional;
                product.UnitOfMeasure = parent.UnitOfMeasure;
                product.MinWholesaleQuantity = parent.MinWholesaleQuantity;
                product.PriceUSD = parent.PriceRetailUSD;
                product.Cost = parent.CostPriceUSD;
                product.ProfitPercentage = parent.ProfitMarginRetail;
            }
        }
        else
        {
            product.IsStockShared = false;
            product.HasIndependentPricing = false;
            product.ConversionFactor = 1.0000m;
        }

        ValidateProductSku(product.SKU, product.IsGroupHeader);

        if (await _context.Products.AnyAsync(p => p.SKU == product.SKU && !p.IsDeleted))
        {
            throw new InvalidOperationException($"Product with SKU {product.SKU} already exists.");
        }

        ValidateAndCalculateProductPrices(product);

        _context.Products.Add(product);
        await _context.SaveChangesAsync();
        InvalidateProductSkuCache(product.SKU);
        if (product.ParentProduct != null) InvalidateProductSkuCache(product.ParentProduct.SKU);
        return product;
    }

    public async Task UpdateProductAsync(Product product)
    {
        EnsureCatalogMutationPermission();

        var existing = await _context.Products.FindAsync(product.Id);
        if (existing == null) throw new KeyNotFoundException($"Product {product.Id} not found");

        var entry = _context.Entry(existing);
        bool originalIsGroupHeader = entry.OriginalValues.GetValue<bool>(nameof(Product.IsGroupHeader));
        bool originalIsStockShared = entry.OriginalValues.GetValue<bool>(nameof(Product.IsStockShared));
        bool originalHasIndependentPricing = entry.OriginalValues.GetValue<bool>(nameof(Product.HasIndependentPricing));
        decimal originalConversionFactor = entry.OriginalValues.GetValue<decimal>(nameof(Product.ConversionFactor));

        if (originalIsGroupHeader)
        {
            if (product.IsStockShared != originalIsStockShared || product.HasIndependentPricing != originalHasIndependentPricing)
            {
                throw new InvalidOperationException("No se permite cambiar las banderas de Stock Compartido o Precios Independientes en un grupo existente.");
            }
        }

        if (!product.IsGroupHeader)
        {
            int activeVariants = await _context.Products.CountAsync(p => p.ParentProductId == product.Id && !p.IsDeleted);
            if (activeVariants > 0)
            {
                throw new InvalidOperationException($"No se puede desmarcar el grupo '{product.Name}' porque tiene {activeVariants} variantes asociadas. Desvincule o elimine las variantes primero.");
            }
            product.IsStockShared = false;
            product.HasIndependentPricing = false;
        }

        if (product.IsCashAdvance)
        {
            if (product.IsGroupHeader)
            {
                throw new ArgumentException("Un producto configurado como Servicio de Adelanto de Efectivo no puede ser un grupo de variantes.");
            }
            if (product.ParentProductId.HasValue)
            {
                throw new ArgumentException("Un producto configurado como Servicio de Adelanto de Efectivo no puede ser variante de un producto padre.");
            }

            product.IsFractional = false;
            product.UnitOfMeasure = UnitOfMeasureType.Und;
            product.StockQuantity = 0m;
            product.ReservedQuantity = 0m;
            product.LowStockThreshold = 0m;
            product.IsStockShared = false;
            product.HasIndependentPricing = false;
            product.ConversionFactor = 1.0000m;
        }
        else if (product.IsGroupHeader)
        {
            product.ConversionFactor = 1.0000m;
            if (string.IsNullOrWhiteSpace(product.SKU))
            {
                product.SKU = existing.SKU;
            }
            product.ParentProductId = null;
            product.ReservedQuantity = 0m;
            if (!product.IsStockShared)
            {
                product.StockQuantity = 0m;
                product.LowStockThreshold = 0m;
            }
            if (string.IsNullOrWhiteSpace(product.GroupKey))
            {
                product.GroupKey = product.Name.Trim();
            }
            if (product.HasIndependentPricing)
            {
                product.CostPriceUSD = 0m;
                product.Cost = 0m;
                product.ProfitMarginRetail = 0m;
                product.ProfitPercentage = 0m;
                product.PriceRetailUSD = 0m;
                product.PriceUSD = 0m;
                product.PriceBsS = 0m;
                product.HasWholesale = false;
                product.ProfitMarginWholesale = 0m;
                product.PriceWholesaleUSD = 0m;
            }
        }
        else if (product.ParentProductId.HasValue)
        {
            var parent = await _context.Products.FindAsync(product.ParentProductId.Value);
            if (parent != null && !parent.IsDeleted)
            {
                product.IsGroupHeader = false;
                product.IsStockShared = false;
                product.HasIndependentPricing = false;

                if (parent.IsStockShared)
                {
                    product.StockQuantity = 0m;
                    product.ReservedQuantity = 0m;
                    product.LowStockThreshold = 0m;
                    decimal factor = product.ConversionFactor > 0 ? product.ConversionFactor : (originalConversionFactor > 0 ? originalConversionFactor : 1.0000m);
                    if (factor < 0.0001m || factor > 1_000_000m)
                    {
                        throw new ArgumentException(Core.Constants.InventoryMessages.ConversionFactorOutOfRange);
                    }
                    product.ConversionFactor = factor;
                }
                else
                {
                    product.ConversionFactor = 1.0000m;
                }

                if (!parent.HasIndependentPricing)
                {
                    // Inherit pricing from parent
                    product.PriceRetailUSD = parent.PriceRetailUSD;
                    product.PriceWholesaleUSD = parent.PriceWholesaleUSD;
                    product.CostPriceUSD = parent.CostPriceUSD;
                    product.ProfitMarginRetail = parent.ProfitMarginRetail;
                    product.ProfitMarginWholesale = parent.ProfitMarginWholesale;
                    product.HasWholesale = parent.HasWholesale;
                    product.IsFractional = parent.IsFractional;
                    product.UnitOfMeasure = parent.UnitOfMeasure;
                    product.MinWholesaleQuantity = parent.MinWholesaleQuantity;
                    product.PriceUSD = parent.PriceRetailUSD;
                    product.Cost = parent.CostPriceUSD;
                    product.ProfitPercentage = parent.ProfitMarginRetail;
                }
            }
            else
            {
                product.ConversionFactor = 1.0000m;
            }
        }
        else
        {
            product.ConversionFactor = 1.0000m;
        }

        ValidateProductSku(product.SKU, product.IsGroupHeader);
        ValidateAndCalculateProductPrices(product);

        // 8.16-H02: la transacción manual debe vivir DENTRO de CreateExecutionStrategy().ExecuteAsync()
        // para no lanzar InvalidOperationException bajo NpgsqlRetryingExecutionStrategy en producción.
        await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = _context.Database.IsRelational() ? await _context.Database.BeginTransactionAsync() : null;

            _context.Entry(existing).CurrentValues.SetValues(product);
            existing.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // If updating a parent product with HasIndependentPricing == false, propagate prices/costs to all active variants in batch
            if (product.IsGroupHeader && !product.HasIndependentPricing)
            {
                if (_context.Database.IsRelational())
                {
                    await _context.Products
                        .Where(p => p.ParentProductId == product.Id && !p.IsDeleted)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(p => p.PriceRetailUSD, product.PriceRetailUSD)
                            .SetProperty(p => p.PriceWholesaleUSD, product.PriceWholesaleUSD)
                            .SetProperty(p => p.CostPriceUSD, product.CostPriceUSD)
                            .SetProperty(p => p.ProfitMarginRetail, product.ProfitMarginRetail)
                            .SetProperty(p => p.ProfitMarginWholesale, product.ProfitMarginWholesale)
                            .SetProperty(p => p.HasWholesale, product.HasWholesale)
                            .SetProperty(p => p.MinWholesaleQuantity, product.MinWholesaleQuantity)
                            .SetProperty(p => p.PriceUSD, product.PriceRetailUSD)
                            .SetProperty(p => p.Cost, product.CostPriceUSD)
                            .SetProperty(p => p.ProfitPercentage, product.ProfitMarginRetail)
                            .SetProperty(p => p.UpdatedAt, DateTime.UtcNow));
                }
                else
                {
                    // In-memory or non-relational fallback
                    var variants = await _context.Products.Where(p => p.ParentProductId == product.Id && !p.IsDeleted).ToListAsync();
                    foreach (var v in variants)
                    {
                        v.PriceRetailUSD = product.PriceRetailUSD;
                        v.PriceWholesaleUSD = product.PriceWholesaleUSD;
                        v.CostPriceUSD = product.CostPriceUSD;
                        v.ProfitMarginRetail = product.ProfitMarginRetail;
                        v.ProfitMarginWholesale = product.ProfitMarginWholesale;
                        v.HasWholesale = product.HasWholesale;
                        v.MinWholesaleQuantity = product.MinWholesaleQuantity;
                        v.PriceUSD = product.PriceRetailUSD;
                        v.Cost = product.CostPriceUSD;
                        v.ProfitPercentage = product.ProfitMarginRetail;
                        v.UpdatedAt = DateTime.UtcNow;
                    }
                    await _context.SaveChangesAsync();
                }
            }

            if (tx != null)
            {
                await tx.CommitAsync();
            }
        });

        InvalidateProductSkuCache(product.SKU);
        if (product.IsGroupHeader || product.ParentProductId != null)
        {
            InvalidateAllProductCaches();
        }
    }

    public async Task SetProductStatusAsync(int id, bool isActive, bool isDeleted)
    {
        EnsureCatalogMutationPermission();
        var product = await _context.Products.FindAsync(id);
        if (product != null)
        {
            product.IsActive = isActive;
            product.IsDeleted = isDeleted;
            product.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            InvalidateProductSkuCache(product.SKU);
            if (product.IsGroupHeader) InvalidateAllProductCaches();
        }
    }

    public async Task RestoreProductAsync(int id)
    {
        EnsureCatalogMutationPermission();
        await SetProductStatusAsync(id, isActive: true, isDeleted: false);
    }

    public async Task<string> DeleteProductAsync(int id, bool forceHardDelete = false)
    {
        EnsureCatalogMutationPermission();
        var product = await _context.Products.FindAsync(id);
        if (product == null) return "not_found";

        // Safe delete rule: Block deleting a parent product if it has active variants
        if (product.IsGroupHeader)
        {
            int activeVariants = await _context.Products.CountAsync(p => p.ParentProductId == id && !p.IsDeleted);
            if (activeVariants > 0)
            {
                throw new InvalidOperationException($"No se puede eliminar el producto padre '{product.Name}' porque contiene {activeVariants} variantes asociadas. Desvincule o elimine primero las variantes.");
            }
        }

        string result = "archived";
        if (forceHardDelete)
        {
            try
            {
                _context.Products.Remove(product);
                await _context.SaveChangesAsync();
                result = "hard_deleted";
            }
            catch (DbUpdateException ex)
            {
                // 8.5-M3: el hard-delete falla solo por integridad referencial (historial contable/FK con
                // Restrict). Se registra el motivo y se degrada a archivar (soft delete) sin enmascarar otras
                // fallas (conexión, OOM, etc.) que se propagan al caller.
                Core.Logging.AppLogger.LogWarn($"[DeleteProductAsync] Hard delete del producto {product.Id}/{product.SKU} degradado a archivo por restricción referencial: {ex.Message}", "Inventory");
                _context.Entry(product).State = Microsoft.EntityFrameworkCore.EntityState.Unchanged;
                product.IsActive = false;
                product.IsDeleted = true;
                product.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                result = "archived";
            }
        }
        else
        {
            // Soft delete -> mark as deleted (archived for accounting)
            product.IsActive = false;
            product.IsDeleted = true;
            product.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            result = "archived";
        }

        InvalidateProductSkuCache(product.SKU);
        if (product.IsGroupHeader || product.ParentProductId != null) InvalidateAllProductCaches();
        return result;
    }
}
