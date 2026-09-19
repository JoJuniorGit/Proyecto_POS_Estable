using Core.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

public partial class InventoryService
{
    private async Task UpdateProductAsync(Product product, System.Threading.CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();

        var existing = await _context.Products.FindAsync(new object[] { product.Id }, cancellationToken);
        if (existing == null) throw new KeyNotFoundException($"Product {product.Id} not found");

        var entry = _context.Entry(existing);
        bool originalIsGroupHeader = entry.OriginalValues.GetValue<bool>(nameof(Product.IsGroupHeader));
        bool originalIsStockShared = entry.OriginalValues.GetValue<bool>(nameof(Product.IsStockShared));
        bool originalHasIndependentPricing = entry.OriginalValues.GetValue<bool>(nameof(Product.HasIndependentPricing));
        decimal originalConversionFactor = entry.OriginalValues.GetValue<decimal>(nameof(Product.ConversionFactor));

        EnsureGroupFlagsUnchanged(product, originalIsGroupHeader, originalIsStockShared, originalHasIndependentPricing);
        await EnsureUnmarkingGroupWithoutVariantsAsync(product, cancellationToken);
        await ApplyUpdateProductRulesAsync(product, existing, originalConversionFactor, cancellationToken);

        ValidateProductSku(product.SKU, product.IsGroupHeader);
        ValidateAndCalculateProductPrices(product);

        await PersistProductUpdateAsync(existing, product, cancellationToken);

        InvalidateProductSkuCache(product.SKU);
        if (product.IsGroupHeader || product.ParentProductId != null)
        {
            InvalidateAllProductCaches();
        }
    }

    private static void EnsureGroupFlagsUnchanged(Product product, bool originalIsGroupHeader, bool originalIsStockShared, bool originalHasIndependentPricing)
    {
        if (originalIsGroupHeader && (product.IsStockShared != originalIsStockShared || product.HasIndependentPricing != originalHasIndependentPricing))
        {
            throw new InvalidOperationException("No se permite cambiar las banderas de Stock Compartido o Precios Independientes en un grupo existente.");
        }
    }

    private async Task EnsureUnmarkingGroupWithoutVariantsAsync(Product product, System.Threading.CancellationToken cancellationToken)
    {
        if (product.IsGroupHeader)
        {
            return;
        }

        int activeVariants = await _context.Products.CountAsync(p => p.ParentProductId == product.Id && !p.IsDeleted, cancellationToken);
        if (activeVariants > 0)
        {
            throw new InvalidOperationException($"No se puede desmarcar el grupo '{product.Name}' porque tiene {activeVariants} variantes asociadas. Desvincule o elimine las variantes primero.");
        }
        product.IsStockShared = false;
        product.HasIndependentPricing = false;
    }

    private async Task ApplyUpdateProductRulesAsync(Product product, Product existing, decimal originalConversionFactor, System.Threading.CancellationToken cancellationToken)
    {
        if (product.IsCashAdvance)
        {
            ApplyCashAdvanceProductRules(product);
        }
        else if (product.IsGroupHeader)
        {
            ApplyGroupHeaderUpdateRules(product, existing);
        }
        else if (product.ParentProductId.HasValue)
        {
            await ApplyVariantUpdateRulesAsync(product, originalConversionFactor, cancellationToken);
        }
        else
        {
            product.ConversionFactor = 1.0000m;
        }
    }

    private static void ApplyGroupHeaderUpdateRules(Product product, Product existing)
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
            ZeroIndependentPricingFields(product);
        }
    }

    private async Task ApplyVariantUpdateRulesAsync(Product product, decimal originalConversionFactor, System.Threading.CancellationToken cancellationToken)
    {
        var parent = await _context.Products.FindAsync(new object[] { product.ParentProductId!.Value }, cancellationToken);
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
                CopyPricingFromParent(product, parent);
            }
        }
        else
        {
            product.ConversionFactor = 1.0000m;
        }
    }

    private async Task PersistProductUpdateAsync(Product existing, Product product, System.Threading.CancellationToken cancellationToken)
    {
        await _context.Database.CreateExecutionStrategy().ExecuteAsync(async _ =>
        {
            await using var tx = _context.Database.IsRelational() ? await _context.Database.BeginTransactionAsync(cancellationToken) : null;

            _context.Entry(existing).CurrentValues.SetValues(product);
            existing.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            if (product.IsGroupHeader && !product.HasIndependentPricing)
            {
                await PropagateGroupPricingToVariantsAsync(product, cancellationToken);
            }

            if (tx != null)
            {
                await tx.CommitAsync(cancellationToken);
            }
        }, cancellationToken);
    }

    private async Task PropagateGroupPricingToVariantsAsync(Product product, System.Threading.CancellationToken cancellationToken)
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
                    .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), cancellationToken);
            return;
        }

        var variants = await _context.Products.Where(p => p.ParentProductId == product.Id && !p.IsDeleted).ToListAsync(cancellationToken);
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
        await _context.SaveChangesAsync(cancellationToken);
    }
}
