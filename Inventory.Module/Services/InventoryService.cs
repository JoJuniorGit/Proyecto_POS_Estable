using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

public class InventoryService : IInventoryService
{
    private readonly InventoryDbContext _context;
    private readonly ICurrentUserService? _currentUserService;

    public InventoryService(InventoryDbContext context, ICurrentUserService? currentUserService = null)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    private void EnsureCatalogMutationPermission()
    {
        if (_currentUserService != null && !_currentUserService.CanMutateCatalog)
        {
            throw new UnauthorizedAccessException("El usuario actual no tiene permisos para modificar el catálogo ni realizar importaciones.");
        }
    }

    public async Task<List<Product>> GetAllProductsAsync()
    {
        return await _context.Products.AsNoTracking().ToListAsync();
    }

    public async Task<decimal> GetTodayExchangeRateAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var record = await _context.ExchangeRateHistory.FirstOrDefaultAsync(r => r.Date == today);
        if (record != null && record.Rate > 0) return record.Rate;

        var lastRecord = await _context.ExchangeRateHistory.OrderByDescending(r => r.Date).FirstOrDefaultAsync();
        return lastRecord?.Rate ?? 0m;
    }

    public async Task<List<Product>> GetProductsByIdsAsync(IEnumerable<int> productIds)
    {
        var idList = productIds.Distinct().ToList();
        if (!idList.Any()) return new List<Product>();
        return await _context.Products.AsNoTracking().Where(p => idList.Contains(p.Id)).ToListAsync();
    }

    public async Task<Product?> GetProductByIdAsync(int id)
    {
        return await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<Product?> GetCashAdvanceProductAsync()
    {
        return await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.IsCashAdvance && !p.IsDeleted && p.IsActive);
    }

    public async Task<Product?> GetProductBySkuAsync(string sku)
    {
        return await _context.Products.FirstOrDefaultAsync(p => p.SKU == sku);
    }

    private void ValidateAndCalculateProductPrices(Product product)
    {
        if (product.MinWholesaleQuantity <= 0m)
        {
            product.MinWholesaleQuantity = 6.000m;
        }

        // Precedence Rule for Retail Price:
        // Manual price entry (> 0) takes absolute precedence over margin calculation
        if (product.PriceRetailUSD > 0)
        {
            if (product.ProfitMarginRetail == 0 && product.CostPriceUSD > 0)
            {
                decimal calculatedProfit = ((product.PriceRetailUSD / product.CostPriceUSD) - 1m) * 100m;
                product.ProfitMarginRetail = calculatedProfit < 0 ? 0 : Math.Round(calculatedProfit, 2, MidpointRounding.AwayFromZero);
            }
        }
        else if (product.CostPriceUSD > 0 && product.ProfitMarginRetail > 0)
        {
            decimal rawPrice = product.CostPriceUSD * (1m + (product.ProfitMarginRetail / 100m));
            product.PriceRetailUSD = Math.Ceiling(rawPrice * 100m) / 100m;
        }

        if (!product.HasWholesale)
        {
            product.PriceWholesaleUSD = product.PriceRetailUSD;
            product.ProfitMarginWholesale = product.ProfitMarginRetail;
        }
        else
        {
            // Precedence Rule for Wholesale Price:
            if (product.PriceWholesaleUSD > 0)
            {
                if (product.ProfitMarginWholesale == 0 && product.CostPriceUSD > 0)
                {
                    decimal calculatedProfit = ((product.PriceWholesaleUSD / product.CostPriceUSD) - 1m) * 100m;
                    product.ProfitMarginWholesale = calculatedProfit < 0 ? 0 : Math.Round(calculatedProfit, 2, MidpointRounding.AwayFromZero);
                }
            }
            else if (product.CostPriceUSD > 0 && product.ProfitMarginWholesale > 0)
            {
                decimal rawWholesalePrice = product.CostPriceUSD * (1m + (product.ProfitMarginWholesale / 100m));
                product.PriceWholesaleUSD = Math.Ceiling(rawWholesalePrice * 100m) / 100m;
            }

            if (product.PriceWholesaleUSD > product.PriceRetailUSD && product.PriceRetailUSD > 0)
            {
                throw new InvalidOperationException($"El precio al mayor (${product.PriceWholesaleUSD:F2}) no puede ser mayor al precio al detal (${product.PriceRetailUSD:F2}).");
            }

            if (product.ProfitMarginWholesale > product.ProfitMarginRetail && product.ProfitMarginRetail > 0)
            {
                throw new InvalidOperationException($"El margen al mayor ({product.ProfitMarginWholesale:F2}%) no puede ser mayor al margen al detal ({product.ProfitMarginRetail:F2}%).");
            }
        }

        if (product.PriceRetailUSD > 0)
        {
            product.PriceUSD = product.PriceRetailUSD;
        }
        if (product.CostPriceUSD > 0)
        {
            product.Cost = product.CostPriceUSD;
        }
        if (product.ProfitMarginRetail > 0)
        {
            product.ProfitPercentage = product.ProfitMarginRetail;
        }
    }

    private static void ValidateProductSku(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku) || !System.Text.RegularExpressions.Regex.IsMatch(sku.Trim(), @"^\d+$"))
        {
            throw new InvalidOperationException("El SKU/Código del producto debe ser un número entero válido (solo dígitos 0-9).");
        }
    }

    public async Task<Product> CreateProductAsync(Product product)
    {
        EnsureCatalogMutationPermission();
        ValidateProductSku(product.SKU);

        if (await _context.Products.AnyAsync(p => p.SKU == product.SKU))
        {
            throw new InvalidOperationException($"Product with SKU {product.SKU} already exists.");
        }

        ValidateAndCalculateProductPrices(product);

        _context.Products.Add(product);
        await _context.SaveChangesAsync();
        return product;
    }

    public async Task UpdateProductAsync(Product product)
    {
        EnsureCatalogMutationPermission();
        ValidateProductSku(product.SKU);

        var existing = await _context.Products.FindAsync(product.Id);
        if (existing == null) throw new KeyNotFoundException($"Product {product.Id} not found");

        ValidateAndCalculateProductPrices(product);

        if (product.RowVersion == null || product.RowVersion.Length == 0)
        {
            product.RowVersion = existing.RowVersion;
        }

        _context.Entry(existing).CurrentValues.SetValues(product);
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
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

        if (forceHardDelete)
        {
            try
            {
                _context.Products.Remove(product);
                await _context.SaveChangesAsync();
                return "hard_deleted";
            }
            catch
            {
                // Has FK relationships (e.g. accounting history), fallback to archived
                _context.Entry(product).State = Microsoft.EntityFrameworkCore.EntityState.Unchanged;
                product.IsActive = false;
                product.IsDeleted = true;
                product.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return "archived";
            }
        }
        else
        {
            // Soft delete -> mark as deleted (archived for accounting)
            product.IsActive = false;
            product.IsDeleted = true;
            product.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return "archived";
        }
    }

    public async Task UpdateStockAsync(int productId, decimal quantityChange, string reason, string? userId = null)
    {
        var product = await _context.Products.FindAsync(productId);
        if (product == null) throw new KeyNotFoundException($"Product {productId} not found");

        if (quantityChange < 0 && (product.StockQuantity + quantityChange) < 0)
        {
            throw new InvalidOperationException($"Stock insuficiente para el producto '{product.Name}' (SKU: {product.SKU}). Stock actual: {product.StockQuantity}, deducción requerida: {Math.Abs(quantityChange)}.");
        }

        product.StockQuantity += quantityChange;

        // Log movement
        var movement = new StockMovement
        {
            ProductId = productId,
            QuantityChange = quantityChange,
            NewStockLevel = product.StockQuantity,
            Reason = reason,
            MovementDate = DateTime.UtcNow,
            UserId = _currentUserService?.UserId ?? userId
        };

        _context.StockMovements.Add(movement);
        product.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
    }

    public async Task<int> ReserveStockAsync(int productId, decimal quantity, TimeSpan duration)
    {
        var product = await _context.Products.FindAsync(productId);
        if (product == null) throw new KeyNotFoundException($"Product {productId} not found");

        // Check availability
        if ((product.StockQuantity - product.ReservedQuantity) < quantity)
        {
            throw new InvalidOperationException("Not enough stock available.");
        }

        product.ReservedQuantity += quantity;

        var reservation = new StockReservation
        {
            ProductId = productId,
            Quantity = quantity,
            ExpiryDate = DateTime.UtcNow.Add(duration),
            IsConfirmed = false
        };

        _context.StockReservations.Add(reservation);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("Stock changed concurrently. Please try again.");
        }

        return reservation.Id;
    }

    public async Task ConfirmReservationAsync(int reservationId, string reason)
    {
        if (reservationId == 0) return; // Ignore service reservations

        var reservation = await _context.StockReservations
            .Include(r => r.Product)
            .FirstOrDefaultAsync(r => r.Id == reservationId);

        if (reservation == null) throw new KeyNotFoundException("Reservation not found.");
        if (reservation.IsConfirmed) return; // Already confirmed

        // Reduce stock and reserved quantity
        reservation.Product.StockQuantity -= reservation.Quantity;
        reservation.Product.ReservedQuantity -= reservation.Quantity;
        reservation.IsConfirmed = true;

        // Log movement
        var movement = new StockMovement
        {
            ProductId = reservation.ProductId,
            QuantityChange = -reservation.Quantity,
            NewStockLevel = reservation.Product.StockQuantity,
            Reason = $"Confirmed Reservation {reservationId}: {reason}",
            MovementDate = DateTime.UtcNow
        };
        _context.StockMovements.Add(movement);
        _context.StockReservations.Remove(reservation); // Cleanup or keep as history? Plan said delete.

        await _context.SaveChangesAsync();
    }

    public async Task CancelReservationAsync(int reservationId)
    {
        if (reservationId == 0) return; // Ignore service reservations

        var reservation = await _context.StockReservations
            .Include(r => r.Product)
            .FirstOrDefaultAsync(r => r.Id == reservationId);

        if (reservation == null) return; // Already gone

        reservation.Product.ReservedQuantity -= reservation.Quantity;
        _context.StockReservations.Remove(reservation);

        await _context.SaveChangesAsync();
    }

    public async Task<List<Core.DTOs.ProductQuickInfoDto>> GetSuggestionsAsync(string filter, bool activeOnly, System.Threading.CancellationToken token)
    {
        IQueryable<Product> query = _context.Products.AsNoTracking();

        if (activeOnly)
        {
            query = query.Where(p => p.IsActive && !p.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            string lower = filter.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(lower) || p.SKU.ToLower().Contains(lower));
        }

        return await query
            .OrderBy(p => p.Name)
            .Select(p => new Core.DTOs.ProductQuickInfoDto
            {
                Id = p.Id,
                Name = p.Name,
                SKU = p.SKU,
                PriceUSD = p.PriceUSD,
                PriceBsS = p.PriceBsS,
                StockQuantity = p.StockQuantity,
                ReservedQuantity = p.ReservedQuantity,
                IsCashAdvance = p.IsCashAdvance,
                IsActive = p.IsActive,
                ProfitPercentage = p.ProfitPercentage
            })
            .Take(10)
            .ToListAsync(token);
    }

    public async Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetProductsPagedAsync(string? filter, int page, int pageSize, string? statusFilter = null, string? sortBy = null, bool isDescending = false, System.Threading.CancellationToken token = default)
    {
        IQueryable<Product> query = _context.Products.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            var status = statusFilter.ToLower().Trim();
            if (status == "active")
            {
                query = query.Where(p => p.IsActive && !p.IsDeleted);
            }
            else if (status == "inactive")
            {
                query = query.Where(p => !p.IsActive && !p.IsDeleted);
            }
            else if (status == "deleted" || status == "archived")
            {
                query = query.Where(p => p.IsDeleted);
            }
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            string lower = filter.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(lower) || p.SKU.ToLower().Contains(lower));
        }

        var totalCount = await query.CountAsync(token);

        query = (sortBy?.ToLower().Trim()) switch
        {
            "name" => isDescending 
                ? query.OrderByDescending(p => p.Name) 
                : query.OrderBy(p => p.Name),

            "price" or "priceretail" => isDescending 
                ? query.OrderByDescending(p => p.PriceRetailUSD > 0 ? p.PriceRetailUSD : p.PriceUSD).ThenByDescending(p => p.Name)
                : query.OrderBy(p => p.PriceRetailUSD > 0 ? p.PriceRetailUSD : p.PriceUSD).ThenBy(p => p.Name),

            "cost" or "costprice" => isDescending 
                ? query.OrderByDescending(p => p.CostPriceUSD > 0 ? p.CostPriceUSD : p.Cost).ThenByDescending(p => p.Name)
                : query.OrderBy(p => p.CostPriceUSD > 0 ? p.CostPriceUSD : p.Cost).ThenBy(p => p.Name),

            "stock" or "stockquantity" => isDescending 
                ? query.OrderByDescending(p => p.StockQuantity).ThenByDescending(p => p.Name)
                : query.OrderBy(p => p.StockQuantity).ThenBy(p => p.Name),

            "sku" or "barcode" => isDescending 
                ? query.OrderByDescending(p => (p.SKU ?? "").Length).ThenByDescending(p => p.SKU)
                : query.OrderBy(p => (p.SKU ?? "").Length).ThenBy(p => p.SKU),

            _ => isDescending ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name)
        };

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new Core.DTOs.ProductDto
            {
                Id = p.Id,
                Name = p.Name,
                SKU = p.SKU,
                Description = p.Description,
                PriceUSD = p.PriceUSD,
                PriceRetailUSD = p.PriceRetailUSD,
                PriceWholesaleUSD = p.PriceWholesaleUSD,
                CostPriceUSD = p.CostPriceUSD,
                ProfitMarginRetail = p.ProfitMarginRetail,
                ProfitMarginWholesale = p.ProfitMarginWholesale,
                MinWholesaleQuantity = p.MinWholesaleQuantity,
                HasWholesale = p.HasWholesale,
                IsFractional = p.IsFractional,
                PriceBsS = p.PriceBsS,
                Cost = p.Cost,
                StockQuantity = p.StockQuantity,
                ProfitPercentage = p.ProfitPercentage,
                UnitOfMeasure = p.UnitOfMeasure,
                LowStockThreshold = p.LowStockThreshold,
                IsCashAdvance = p.IsCashAdvance,
                IsActive = p.IsActive,
                IsDeleted = p.IsDeleted,
                ReservedQuantity = p.ReservedQuantity
            })

            .ToListAsync(token);

        return new Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>
        {
            Items = items,
            TotalCount = totalCount,
            HasMore = (page * pageSize) < totalCount
        };
    }

    public async Task<Core.DTOs.ProductQuickInfoDto?> GetProductQuickInfoAsync(string sku)
    {
        return await _context.Products
            .AsNoTracking()
            .Where(p => p.SKU == sku)
            .Select(p => new Core.DTOs.ProductQuickInfoDto
            {
                Id = p.Id,
                SKU = p.SKU,
                Name = p.Name,
                PriceUSD = p.PriceUSD,
                PriceBsS = p.PriceBsS,
                StockQuantity = p.StockQuantity,
                ReservedQuantity = p.ReservedQuantity,
                IsCashAdvance = p.IsCashAdvance,
                IsActive = p.IsActive,
                ProfitPercentage = p.ProfitPercentage
            })
            .FirstOrDefaultAsync();
    }

    public async Task<(int added, int updated)> BulkImportProductsAsync(IEnumerable<Core.DTOs.ProductImportDto> products, bool overwriteMerge, System.Threading.CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        int added = 0;
        int updated = 0;

        using var transaction = _context.Database.IsRelational() ? await _context.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var skusToImport = products.Where(p => p.IsValid && !string.IsNullOrWhiteSpace(p.SKU)).Select(p => p.SKU.Trim()).Distinct().ToList();
            var existingProducts = await _context.Products
                .Where(p => skusToImport.Contains(p.SKU))
                .ToDictionaryAsync(p => p.SKU, cancellationToken);

            foreach (var dto in products)
            {
                if (!dto.IsValid || string.IsNullOrWhiteSpace(dto.SKU)) continue;

                var (cost, marginRetail, priceRetail, marginWholesale, priceWholesale, minWholesaleQty, hasWholesale) = ResolveImportPricing(dto);
                var (unitEnum, isFractional) = ResolveUnitOfMeasure(dto.UnitOfMeasure, dto.IsFractional);

                var skuClean = dto.SKU.Trim();
                if (existingProducts.TryGetValue(skuClean, out var existingProduct))
                {
                    if (overwriteMerge)
                    {
                        existingProduct.Name = dto.Name.Trim();
                        existingProduct.Description = dto.Description?.Trim() ?? string.Empty;
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
                        existingProduct.LowStockThreshold = Math.Max(0, dto.LowStockThreshold);
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
                        CostPriceUSD = cost,
                        Cost = cost,
                        ProfitMarginRetail = marginRetail,
                        ProfitPercentage = marginRetail,
                        PriceRetailUSD = priceRetail,
                        PriceUSD = priceRetail,
                        ProfitMarginWholesale = marginWholesale,
                        PriceWholesaleUSD = priceWholesale,
                        MinWholesaleQuantity = minWholesaleQty,
                        HasWholesale = hasWholesale,
                        IsFractional = isFractional,
                        UnitOfMeasure = unitEnum,
                        StockQuantity = Math.Max(0, dto.StockQuantity),
                        LowStockThreshold = Math.Max(0, dto.LowStockThreshold),
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };
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
    }

    public async Task<byte[]> ExportProductsAsync(string format, bool activeOnly, string? filter = null, System.Threading.CancellationToken cancellationToken = default)
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

            using var memoryStream = new System.IO.MemoryStream();
            workbook.SaveAs(memoryStream);
            return memoryStream.ToArray();
        }
        else
        {
            var csvBuilder = new System.Text.StringBuilder();
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

            return System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString())).ToArray();
        }
    }

    public Task<byte[]> GenerateTemplateAsync(string format, System.Threading.CancellationToken cancellationToken = default)
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

            using var memoryStream = new System.IO.MemoryStream();
            workbook.SaveAs(memoryStream);
            return Task.FromResult(memoryStream.ToArray());
        }
        else
        {
            var csvBuilder = new System.Text.StringBuilder();
            csvBuilder.AppendLine("SKU;Nombre;Descripción;CostoUSD;MargenDetal%;MargenMayor%;CantMinMayorista;HabilitarMayorista;EsFraccionable;StockActual;UmbralMinimo");
            csvBuilder.AppendLine("1001;Producto Ejemplo;Descripción breve de ejemplo;10.00;30.00;20.00;6.000;SI;NO;100;5");

            return Task.FromResult(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString())).ToArray());
        }
    }

    private static (decimal cost, decimal marginRetail, decimal priceRetail, decimal marginWholesale, decimal priceWholesale, decimal minWholesaleQty, bool hasWholesale) ResolveImportPricing(Core.DTOs.ProductImportDto dto)
    {
        decimal cost = Math.Max(0m, dto.CostPriceUSD);
        decimal marginRetail = Math.Max(0m, dto.ProfitMarginRetail);
        decimal priceRetail = cost > 0 && marginRetail > 0 ? Math.Ceiling(cost * (1m + marginRetail / 100m) * 100m) / 100m : dto.PriceRetailUSD;

        bool hasWholesale = dto.HasWholesale;
        decimal marginWholesale = 0m;
        decimal priceWholesale = 0m;
        decimal minWholesaleQty = 0m;

        if (hasWholesale)
        {
            marginWholesale = Math.Max(0m, dto.ProfitMarginWholesale);
            priceWholesale = cost > 0 && marginWholesale > 0 ? Math.Ceiling(cost * (1m + marginWholesale / 100m) * 100m) / 100m : dto.PriceWholesaleUSD;

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
        var unit = isFractionalInput ? UnitOfMeasureType.Kg : UnitOfMeasureType.Und;
        return (unit, isFractionalInput);
    }

    private static string EscapeCsvField(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";

        // Neutralize CSV/Excel Formula Injection (CWE-1236)
        if (field.StartsWith('=') || field.StartsWith('+') || field.StartsWith('-') || field.StartsWith('@') || field.StartsWith('\t') || field.StartsWith('\r'))
        {
            field = "'" + field;
        }

        if (field.Contains(";") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }
}
