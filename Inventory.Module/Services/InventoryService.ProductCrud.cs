using Core.DTOs;
using Core.Entities;
using Core.Extensions;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

public partial class InventoryService : IProductManagementService, IReservationService
{
    public async Task<ProductDto> CreateProductFromDtoAsync(CreateProductDto request, System.Threading.CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();

        decimal retailUsd = request.PriceRetailUSD > 0 ? request.PriceRetailUSD : request.PriceUSD;
        decimal todayRate = await GetTodayExchangeRateAsync(cancellationToken);
        decimal canonicalPriceBsS = todayRate > 0
            ? Core.Helpers.PricingCalculator.ToBsSCeiling(retailUsd, todayRate)
            : Core.Helpers.PricingCalculator.RoundPriceUp(request.PriceBsS);

        var product = request.ToEntity(canonicalPriceBsS);
        var created = await CreateProductAsync(product, cancellationToken);
        return created.ToDto(canViewCost: true);
    }

    public async Task UpdateProductFromDtoAsync(int id, UpdateProductDto request, System.Threading.CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();

        var existing = await _context.Products.FindAsync(new object[] { id }, cancellationToken);
        if (existing == null) throw new KeyNotFoundException($"Producto con ID {id} no encontrado.");

        decimal todayRate = await GetTodayExchangeRateAsync(cancellationToken);
        decimal canonicalPriceBsS = (request.PriceRetailUSD > 0 && todayRate > 0)
            ? Core.Helpers.PricingCalculator.ToBsSCeiling(request.PriceRetailUSD, todayRate)
            : (request.PriceBsS > 0 ? Core.Helpers.PricingCalculator.RoundPriceUp(request.PriceBsS) : existing.PriceBsS);

        existing.UpdateFromDto(request, canonicalPriceBsS);
        await UpdateProductAsync(existing, cancellationToken);
    }

    public async Task<ProductDto?> GetProductDtoByIdAsync(int id, System.Threading.CancellationToken cancellationToken = default)
    {
        var product = await _context.Products
            .AsNoTracking()
            .Include(p => p.ParentProduct)
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        return product != null ? product.ToDto(canViewCost: true) : null;
    }

    private async Task<Product> CreateProductAsync(Product product, System.Threading.CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        return await CreateProductCoreAsync(product, cancellationToken);
    }

    // System-internal provisioning (e.g. the cash-advance infrastructure product) must not require the acting user's catalog permission.
    public async Task<int> CreateSystemProductAsync(CreateSystemProductRequest request, System.Threading.CancellationToken cancellationToken = default)
    {
        var product = new Product
        {
            Name = request.Name,
            SKU = request.SKU,
            Description = request.Description ?? string.Empty,
            PriceRetailUSD = request.PriceRetailUSD,
            PriceUSD = request.PriceRetailUSD,
            StockQuantity = request.StockQuantity,
            IsCashAdvance = request.IsCashAdvance,
            IsActive = request.IsActive
        };

        var created = await CreateProductCoreAsync(product, cancellationToken);
        return created.Id;
    }

    private async Task<Product> CreateProductCoreAsync(Product product, System.Threading.CancellationToken cancellationToken)
    {
        if (product.IsCashAdvance)
        {
            ApplyCashAdvanceProductRules(product);
        }
        else if (product.IsGroupHeader)
        {
            ApplyGroupHeaderCreateRules(product);
        }
        else if (product.ParentProductId.HasValue)
        {
            await ApplyVariantCreateRulesAsync(product, cancellationToken);
        }
        else
        {
            ApplyStandaloneProductRules(product);
        }

        ValidateProductSku(product.SKU, product.IsGroupHeader);

        if (await _context.Products.AnyAsync(p => p.SKU == product.SKU && !p.IsDeleted, cancellationToken))
        {
            throw new InvalidOperationException($"Product with SKU {product.SKU} already exists.");
        }

        ValidateAndCalculateProductPrices(product);

        _context.Products.Add(product);
        AddInitialStockMovement(product);

        await _context.SaveChangesAsync(cancellationToken);
        InvalidateProductSkuCache(product.SKU);
        if (product.ParentProduct != null) InvalidateProductSkuCache(product.ParentProduct.SKU);
        return product;
    }

    private static void ApplyCashAdvanceProductRules(Product product)
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

    private static void ApplyGroupHeaderCreateRules(Product product)
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
            ZeroIndependentPricingFields(product);
        }
    }

    private async Task ApplyVariantCreateRulesAsync(Product product, System.Threading.CancellationToken cancellationToken)
    {
        var parent = await _context.Products.FindAsync(new object[] { product.ParentProductId!.Value }, cancellationToken);
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
            CopyPricingFromParent(product, parent);
        }
    }

    private static void ApplyStandaloneProductRules(Product product)
    {
        product.IsStockShared = false;
        product.HasIndependentPricing = false;
        product.ConversionFactor = 1.0000m;
    }

    private static void CopyPricingFromParent(Product product, Product parent)
    {
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

    private static void ZeroIndependentPricingFields(Product product)
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

    private void AddInitialStockMovement(Product product)
    {
        if (product.StockQuantity > 0)
        {
            _context.StockMovements.Add(new StockMovement
            {
                Product = product,
                QuantityChange = product.StockQuantity,
                NewStockLevel = product.StockQuantity,
                Reason = "Carga inicial",
                SaleId = null,
                MovementDate = DateTime.UtcNow,
                UserId = _currentUserService?.UserId
            });
        }
    }

    public async Task SetProductStatusAsync(int id, bool isActive, bool isDeleted, System.Threading.CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        var product = await _context.Products.FindAsync(new object[] { id }, cancellationToken);
        if (product != null)
        {
            product.IsActive = isActive;
            product.IsDeleted = isDeleted;
            product.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            InvalidateProductSkuCache(product.SKU);
            if (product.IsGroupHeader) InvalidateAllProductCaches();
        }
    }

    public async Task RestoreProductAsync(int id, System.Threading.CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        await SetProductStatusAsync(id, isActive: true, isDeleted: false, cancellationToken);
    }

    public async Task<string> DeleteProductAsync(int id, bool forceHardDelete = false, System.Threading.CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        var product = await _context.Products.FindAsync(new object[] { id }, cancellationToken);
        if (product == null) return "not_found";

        if (product.IsGroupHeader)
        {
            int activeVariants = await _context.Products.CountAsync(p => p.ParentProductId == id && !p.IsDeleted, cancellationToken);
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
                await _context.SaveChangesAsync(cancellationToken);
                result = "hard_deleted";
            }
            catch (DbUpdateException ex)
            {
                Core.Logging.AppLogger.LogWarn($"[DeleteProductAsync] Hard delete del producto {product.Id}/{product.SKU} degradado a archivo por restricción referencial: {ex.Message}", "Inventory");
                _context.Entry(product).State = Microsoft.EntityFrameworkCore.EntityState.Unchanged;
                product.IsActive = false;
                product.IsDeleted = true;
                product.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
                result = "archived";
            }
        }
        else
        {
            product.IsActive = false;
            product.IsDeleted = true;
            product.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            result = "archived";
        }

        InvalidateProductSkuCache(product.SKU);
        if (product.IsGroupHeader || product.ParentProductId != null) InvalidateAllProductCaches();
        return result;
    }

    public async Task<int> GetActiveReservationsCountAsync(string referenceId, System.Threading.CancellationToken cancellationToken = default)
    {
        return await _context.StockReservations
            .AsNoTracking()
            .CountAsync(r => !r.IsConfirmed && r.ExpiryDate > DateTime.UtcNow && r.ReferenceId == referenceId, cancellationToken);
    }

    public async Task<bool?> ValidateReservationOwnershipAsync(int reservationId, string userRef, System.Threading.CancellationToken cancellationToken = default)
    {
        var reservation = await _context.StockReservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation == null) return null;
        return reservation.ReferenceId != null && reservation.ReferenceId == userRef;
    }
}
