using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.181-T1 (S1-S4): edición masiva de precios — preview sin escrituras y apply atómico con el
// MISMO motor de cálculo (S4). Cada test verifica además que el preview no persiste nada.
public class BulkPriceUpdateTests
{
    private static InventoryService CreateService(InventoryDbContext context, IMemoryCache? cache = null, bool canMutateCatalog = true)
    {
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.CanMutateCatalog).Returns(canMutateCatalog);
        user.Setup(u => u.UserId).Returns("admin-1");
        return new InventoryService(context, user.Object, cache);
    }

    private static async Task SeedRateAsync(InventoryDbContext context, decimal rate)
    {
        context.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = Core.Helpers.TimeZoneHelper.GetVenezuelaDate(),
            Rate = rate,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static BulkPriceOperationDto Op(BulkPriceField field, BulkPriceMode mode, decimal value) =>
        new() { Field = field, Mode = mode, Value = value };

    private static BulkPriceUpdateRequestDto Request(int[] ids, params BulkPriceOperationDto[] operations) => new()
    {
        ProductIds = ids.ToList(),
        Operations = operations.ToList()
    };

    private static BulkPricePreviewRowDto RowOf(BulkPriceUpdateResultDto result, int productId) =>
        result.Rows.Single(r => r.ProductId == productId);

    private static BulkPriceFieldChangeDto ChangeOf(BulkPricePreviewRowDto row, BulkPriceField field) =>
        row.Changes.Single(c => c.Field == field);

    // ---------------------------------------------------------------------------------------------
    // S1/S4: preview — antes/después con Bs.S canónico, sin escrituras.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_SetFixedRetailPrice_ReturnsBeforeAfterWithCanonicalBsS_AndDoesNotPersist()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 842.21m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Grano Fino",
            SKU = "GRANO001",
            CostPriceUSD = 0.60m,
            ProfitMarginRetail = 35m,
            PriceRetailUSD = 0.81m
        });

        var result = await service.PreviewBulkPriceUpdateAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1.00m)));

        Assert.Equal(1, result.SelectedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(842.21m, result.ExchangeRate);

        var row = Assert.Single(result.Rows);
        Assert.Null(row.SkipReason);
        Assert.False(row.IsError);
        var change = Assert.Single(row.Changes);
        Assert.Equal(BulkPriceField.RetailPrice, change.Field);
        Assert.Equal(0.81m, change.BeforeValue);
        Assert.Equal(1.00m, change.AfterValue);
        Assert.Equal(682.20m, change.BeforeBsS);
        Assert.Equal(842.21m, change.AfterBsS);

        // Sin escrituras: el preview no toca la base.
        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(0.81m, persisted.PriceRetailUSD);
        Assert.Equal(682.20m, persisted.PriceBsS);
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_IncreasePercent_RoundsPriceUpToCeiling()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Cafe Molido",
            SKU = "CAFE01",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 33.33m
        });

        var result = await service.PreviewBulkPriceUpdateAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.IncreasePercent, 10m)));

        var change = ChangeOf(RowOf(result, created.Id), BulkPriceField.RetailPrice);
        Assert.Equal(1.34m, change.BeforeValue);
        Assert.Equal(1.48m, change.AfterValue); // 1.34 * 1.10 = 1.474 -> techo 1.48
        Assert.Equal(148.00m, change.AfterBsS);
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_IncreasePercentMargin_RoundsMarginAndRecalculatesPrice()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Cafe Molido",
            SKU = "CAFE01",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 33.33m
        });

        var result = await service.PreviewBulkPriceUpdateAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailMargin, BulkPriceMode.IncreasePercent, 10m)));

        var row = RowOf(result, created.Id);
        var marginChange = ChangeOf(row, BulkPriceField.RetailMargin);
        Assert.Equal(33.33m, marginChange.BeforeValue);
        Assert.Equal(36.66m, marginChange.AfterValue); // 33.33 * 1.10 = 36.663 -> AwayFromZero 36.66

        // S2: el margen recalcula el precio = costo x (1 + margen) con techo.
        var priceChange = ChangeOf(row, BulkPriceField.RetailPrice);
        Assert.Equal(1.34m, priceChange.BeforeValue);
        Assert.Equal(1.37m, priceChange.AfterValue); // 1.00 * 1.3666 = 1.3666 -> techo 1.37
    }

    // ---------------------------------------------------------------------------------------------
    // S1/S2: apply — persistencia, Bs.S canónico y modo porcentaje.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BulkUpdatePricesAsync_SetFixedRetailPrice_PersistsCanonicalBsSAndNotConversionRate()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 842.21m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Grano Fino",
            SKU = "GRANO001",
            CostPriceUSD = 0.60m,
            ProfitMarginRetail = 35m,
            PriceRetailUSD = 0.81m
        });

        var result = await service.BulkUpdatePricesAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 0.80m)));

        Assert.Equal(1, result.UpdatedCount);
        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(0.80m, persisted.PriceRetailUSD);
        Assert.Equal(0.80m, persisted.PriceUSD);
        Assert.Equal(673.77m, persisted.PriceBsS); // 0.80 * 842.21 = 673.768 -> techo 673.77
        // S2: LastConversionRate no se escribe (igual que create/update actuales).
        Assert.Equal(0m, persisted.LastConversionRate);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_SetFixedRetailMargin_RecalculatesPriceFromCostPlusMargin()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Harina",
            SKU = "HARINA01",
            CostPriceUSD = 2.00m,
            ProfitMarginRetail = 30m
        });

        await service.BulkUpdatePricesAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailMargin, BulkPriceMode.SetFixed, 50m)));

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(50m, persisted.ProfitMarginRetail);
        Assert.Equal(3.00m, persisted.PriceRetailUSD); // 2.00 * 1.50 = 3.00
        Assert.Equal(3.00m, persisted.PriceUSD);
        Assert.Equal(300.00m, persisted.PriceBsS);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_CostPriceIncrease_DoesNotRecalculateRetailPrice()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Harina",
            SKU = "HARINA01",
            CostPriceUSD = 2.00m,
            ProfitMarginRetail = 50m,
            PriceRetailUSD = 3.00m
        });
        var before = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(3.00m, before.PriceRetailUSD);
        Assert.Equal(300.00m, before.PriceBsS);

        await service.BulkUpdatePricesAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.CostPrice, BulkPriceMode.IncreasePercent, 10m)));

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(2.20m, persisted.CostPriceUSD);
        Assert.Equal(2.20m, persisted.Cost);
        Assert.Equal(3.00m, persisted.PriceRetailUSD);
        Assert.Equal(50m, persisted.ProfitMarginRetail);
        Assert.Equal(300.00m, persisted.PriceBsS);
    }

    // ---------------------------------------------------------------------------------------------
    // S1/S2: errores duros — apply 400 all-or-nothing; el preview los muestra como fila con motivo.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_WholesaleAboveRetail_MarksRowAsErrorAndStillShowsTheValidOne()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);

        var valid = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Valido",
            SKU = "VAL-01",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 100m,
            PriceRetailUSD = 2.00m
        });
        var withWholesale = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Mayorista",
            SKU = "MAY-01",
            CostPriceUSD = 5.00m,
            PriceRetailUSD = 10.00m,
            PriceWholesaleUSD = 8.00m,
            HasWholesale = true
        });

        var result = await service.PreviewBulkPriceUpdateAsync(
            Request(new[] { valid.Id, withWholesale.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 5.00m)));

        Assert.Equal(2, result.SelectedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, result.SkippedCount);

        var errorRow = RowOf(result, withWholesale.Id);
        Assert.True(errorRow.IsError);
        Assert.Contains("mayor", errorRow.SkipReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(errorRow.Changes);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_WholesaleAboveRetail_ThrowsAndWritesNothing()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);

        var valid = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Valido",
            SKU = "VAL-01",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 100m,
            PriceRetailUSD = 2.00m
        });
        var withWholesale = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Mayorista",
            SKU = "MAY-01",
            CostPriceUSD = 5.00m,
            PriceRetailUSD = 10.00m,
            PriceWholesaleUSD = 8.00m,
            HasWholesale = true
        });

        await Assert.ThrowsAsync<ArgumentException>(() => service.BulkUpdatePricesAsync(
            Request(new[] { valid.Id, withWholesale.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 5.00m))));

        // All-or-nothing: ni el producto válido ni el inválido se escriben.
        var persistedValid = await context.Products.AsNoTracking().SingleAsync(p => p.Id == valid.Id);
        Assert.Equal(2.00m, persistedValid.PriceRetailUSD);
        var persistedWholesale = await context.Products.AsNoTracking().SingleAsync(p => p.Id == withWholesale.Id);
        Assert.Equal(10.00m, persistedWholesale.PriceRetailUSD);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_DecreasePercentResultNegative_ThrowsAndWritesNothing()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Negativo",
            SKU = "NEG-01",
            CostPriceUSD = 5.00m,
            ProfitMarginRetail = 100m,
            PriceRetailUSD = 10.00m
        });

        var preview = await service.PreviewBulkPriceUpdateAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.DecreasePercent, 150m)));
        var row = Assert.Single(preview.Rows);
        Assert.True(row.IsError);
        Assert.Contains("negativo", row.SkipReason, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<ArgumentException>(() => service.BulkUpdatePricesAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.DecreasePercent, 150m))));

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(10.00m, persisted.PriceRetailUSD);
    }

    // ---------------------------------------------------------------------------------------------
    // S3: agrupadores y variantes.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BulkUpdatePricesAsync_GroupHeaderWithIndependentPricing_IsSkippedWithReason()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);

        var group = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Camisas",
            IsGroupHeader = true,
            HasIndependentPricing = true,
            StockQuantity = 0m
        });
        var variant = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Camisa M",
            SKU = "CAM-M",
            ParentProductId = group.Id,
            CostPriceUSD = 2.00m,
            PriceRetailUSD = 5.00m
        });

        var result = await service.BulkUpdatePricesAsync(
            Request(new[] { group.Id, variant.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 6.00m)));

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, result.SkippedCount);
        var groupRow = RowOf(result, group.Id);
        Assert.False(groupRow.IsError);
        Assert.Contains("independientes", groupRow.SkipReason, StringComparison.OrdinalIgnoreCase);

        var persistedVariant = await context.Products.AsNoTracking().SingleAsync(p => p.Id == variant.Id);
        Assert.Equal(6.00m, persistedVariant.PriceRetailUSD);
        var persistedGroup = await context.Products.AsNoTracking().SingleAsync(p => p.Id == group.Id);
        Assert.Equal(0m, persistedGroup.PriceRetailUSD); // invariante: grupo independiente sin precios propios
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_InheritedGroupHeader_PropagatesPricingToVariantsWithCanonicalBsS()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);

        var group = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Refrescos 2L",
            IsGroupHeader = true,
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 50m,
            PriceRetailUSD = 1.50m
        });
        var first = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Refresco Fresa",
            SKU = "REF-FRESA",
            ParentProductId = group.Id
        });
        var second = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Refresco Chocolate",
            SKU = "REF-CHOCO",
            ParentProductId = group.Id
        });

        var result = await service.BulkUpdatePricesAsync(
            Request(new[] { group.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 2.00m)));

        Assert.Equal(1, result.UpdatedCount);
        foreach (var variantId in new[] { first.Id, second.Id })
        {
            var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == variantId);
            Assert.Equal(2.00m, persisted.PriceRetailUSD);
            Assert.Equal(2.00m, persisted.PriceUSD);
            Assert.Equal(1.00m, persisted.CostPriceUSD);
            Assert.Equal(200.00m, persisted.PriceBsS); // recalculado con la tasa del día (S3)
        }
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_ExplicitlySelectedInheritedVariant_IsSkippedWithReason()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);

        var group = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Refrescos 2L",
            IsGroupHeader = true,
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 50m,
            PriceRetailUSD = 1.50m
        });
        var variant = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Refresco Fresa",
            SKU = "REF-FRESA",
            ParentProductId = group.Id
        });

        var result = await service.BulkUpdatePricesAsync(
            Request(new[] { variant.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 9.00m)));

        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.SkippedCount);
        var row = Assert.Single(result.Rows);
        Assert.Contains("hereda", row.SkipReason, StringComparison.OrdinalIgnoreCase);

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == variant.Id);
        Assert.Equal(1.50m, persisted.PriceRetailUSD);
    }

    // ---------------------------------------------------------------------------------------------
    // S1: selección por filtro ("todos los filtrados").
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BulkUpdatePricesAsync_SelectAllFiltered_UpdatesEveryMatchingProduct()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);

        var first = await service.CreateProductFromDtoAsync(new CreateProductDto { Name = "Cereal A", SKU = "CER-A", CostPriceUSD = 1m, ProfitMarginRetail = 100m, PriceRetailUSD = 2m });
        var second = await service.CreateProductFromDtoAsync(new CreateProductDto { Name = "Cereal B", SKU = "CER-B", CostPriceUSD = 1m, ProfitMarginRetail = 100m, PriceRetailUSD = 2m });
        var other = await service.CreateProductFromDtoAsync(new CreateProductDto { Name = "Jugo", SKU = "JUG-01", CostPriceUSD = 1m, ProfitMarginRetail = 100m, PriceRetailUSD = 2m });

        var request = new BulkPriceUpdateRequestDto
        {
            SelectAllFiltered = true,
            Filter = new BulkPriceSelectionFilterDto { SearchText = "Cereal" },
            Operations = new List<BulkPriceOperationDto> { Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 3m) }
        };

        var result = await service.BulkUpdatePricesAsync(request);

        Assert.Equal(2, result.SelectedCount);
        Assert.Equal(2, result.UpdatedCount);
        var persistedFirst = await context.Products.AsNoTracking().SingleAsync(p => p.Id == first.Id);
        var persistedSecond = await context.Products.AsNoTracking().SingleAsync(p => p.Id == second.Id);
        var persistedOther = await context.Products.AsNoTracking().SingleAsync(p => p.Id == other.Id);
        Assert.Equal(3m, persistedFirst.PriceRetailUSD);
        Assert.Equal(3m, persistedSecond.PriceRetailUSD);
        Assert.Equal(2m, persistedOther.PriceRetailUSD);
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_FilteredSelectionAboveCap_Throws()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);

        context.Products.AddRange(Enumerable.Range(1, 1001).Select(i => new Product
        {
            Name = $"Cap-{i:D4}",
            SKU = $"CAP{i:D4}",
            PriceRetailUSD = 1m,
            CostPriceUSD = 1m,
            IsActive = true
        }));
        await context.SaveChangesAsync();

        var request = new BulkPriceUpdateRequestDto
        {
            SelectAllFiltered = true,
            Filter = new BulkPriceSelectionFilterDto { SearchText = "Cap-" },
            Operations = new List<BulkPriceOperationDto> { Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 2m) }
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewBulkPriceUpdateAsync(request));
        Assert.Contains("excede", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // S4: consistencia preview == apply.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BulkUpdatePricesAsync_PreviewAndApplyShareTheSameEngine_ResultsMatchExactly()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 842.21m);

        var simple = await service.CreateProductFromDtoAsync(new CreateProductDto { Name = "Simple", SKU = "SIMPLE1", CostPriceUSD = 0.60m, ProfitMarginRetail = 35m, PriceRetailUSD = 0.81m });
        var wholesale = await service.CreateProductFromDtoAsync(new CreateProductDto { Name = "Mayor", SKU = "MAYOR1", CostPriceUSD = 5m, PriceRetailUSD = 10m, PriceWholesaleUSD = 8m, HasWholesale = true });
        var group = await service.CreateProductFromDtoAsync(new CreateProductDto { Name = "Grupo", IsGroupHeader = true, CostPriceUSD = 1m, ProfitMarginRetail = 50m, PriceRetailUSD = 1.5m });
        var inherited = await service.CreateProductFromDtoAsync(new CreateProductDto { Name = "Hijo", SKU = "HIJO01", ParentProductId = group.Id });

        var request = Request(
            new[] { simple.Id, wholesale.Id, group.Id, inherited.Id },
            Op(BulkPriceField.RetailPrice, BulkPriceMode.IncreasePercent, 10m));

        var preview = await service.PreviewBulkPriceUpdateAsync(request);
        var applied = await service.BulkUpdatePricesAsync(request);

        Assert.Equal(preview.SelectedCount, applied.SelectedCount);
        Assert.Equal(preview.UpdatedCount, applied.UpdatedCount);
        Assert.Equal(preview.SkippedCount, applied.SkippedCount);
        Assert.Equal(preview.ExchangeRate, applied.ExchangeRate);

        foreach (var previewRow in preview.Rows)
        {
            var appliedRow = RowOf(applied, previewRow.ProductId);
            Assert.Equal(previewRow.SkipReason, appliedRow.SkipReason);
            Assert.Equal(previewRow.IsError, appliedRow.IsError);
            Assert.Equal(
                previewRow.Changes.Select(c => (c.Field, c.BeforeValue, c.AfterValue, c.BeforeBsS, c.AfterBsS)),
                appliedRow.Changes.Select(c => (c.Field, c.BeforeValue, c.AfterValue, c.BeforeBsS, c.AfterBsS)));
        }

        // Y la base quedó exactamente en los valores "después" del preview.
        foreach (var row in preview.Rows.Where(r => r.SkipReason == null))
        {
            var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == row.ProductId);
            var retailChange = row.Changes.SingleOrDefault(c => c.Field == BulkPriceField.RetailPrice);
            if (retailChange != null)
            {
                Assert.Equal(retailChange.AfterValue, persisted.PriceRetailUSD);
                Assert.Equal(retailChange.AfterBsS, persisted.PriceBsS);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // S1: invalidación de cachés post-commit.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BulkUpdatePricesAsync_AfterCommit_InvalidatesRegisteredProductCaches()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(context, cache);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto { Name = "Cache", SKU = "CACHE01", CostPriceUSD = 1m, ProfitMarginRetail = 100m, PriceRetailUSD = 2m });

        var quickInfo = await service.GetProductQuickInfoAsync("CACHE01");
        Assert.NotNull(quickInfo);
        Assert.True(cache.TryGetValue("product_quick_CACHE01", out _));

        await service.BulkUpdatePricesAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 3m)));

        Assert.False(cache.TryGetValue("product_quick_CACHE01", out _));
    }

    // ---------------------------------------------------------------------------------------------
    // S1/S2: validaciones del contrato (400) y omisiones de negocio.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BulkUpdatePricesAsync_FixedPriceWinsOverMarginOperation()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Fijo",
            SKU = "FIJO01",
            CostPriceUSD = 2.00m,
            ProfitMarginRetail = 30m,
            PriceRetailUSD = 2.60m
        });

        // S2 "precio fijo manda": el margen no debe pisar el precio fijado por la misma solicitud.
        await service.BulkUpdatePricesAsync(Request(new[] { created.Id },
            Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 5.00m),
            Op(BulkPriceField.RetailMargin, BulkPriceMode.SetFixed, 90m)));

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(5.00m, persisted.PriceRetailUSD);
        Assert.Equal(90m, persisted.ProfitMarginRetail);
        Assert.Equal(500.00m, persisted.PriceBsS);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_WholesalePriceChange_DoesNotTouchCanonicalBsS()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Mayorista",
            SKU = "MAY-01",
            CostPriceUSD = 5.00m,
            PriceRetailUSD = 10.00m,
            PriceWholesaleUSD = 8.00m,
            HasWholesale = true
        });

        var result = await service.BulkUpdatePricesAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.WholesalePrice, BulkPriceMode.SetFixed, 9.00m)));

        var row = Assert.Single(result.Rows);
        var change = ChangeOf(row, BulkPriceField.WholesalePrice);
        Assert.Equal(8.00m, change.BeforeValue);
        Assert.Equal(9.00m, change.AfterValue);
        Assert.Null(change.BeforeBsS);
        Assert.Null(change.AfterBsS);

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(9.00m, persisted.PriceWholesaleUSD);
        Assert.Equal(10.00m, persisted.PriceRetailUSD);
        Assert.Equal(1000.00m, persisted.PriceBsS); // el Bs.S es el tag del detal: no cambia
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_ResultAbovePriceCap_IsErrorAndApplyThrows()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Tope",
            SKU = "TOPE01",
            CostPriceUSD = 5.00m,
            ProfitMarginRetail = 100m,
            PriceRetailUSD = 10.00m
        });

        var preview = await service.PreviewBulkPriceUpdateAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1_100_000m)));
        var row = Assert.Single(preview.Rows);
        Assert.True(row.IsError);
        Assert.Contains("excede", row.SkipReason, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<ArgumentException>(() => service.BulkUpdatePricesAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1_100_000m))));

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(10.00m, persisted.PriceRetailUSD);
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_ResultAboveMarginCap_IsError()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Tope Margen",
            SKU = "TOPEM01",
            CostPriceUSD = 5.00m,
            ProfitMarginRetail = 100m,
            PriceRetailUSD = 10.00m
        });

        var preview = await service.PreviewBulkPriceUpdateAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailMargin, BulkPriceMode.SetFixed, 1500m)));

        var row = Assert.Single(preview.Rows);
        Assert.True(row.IsError);
        Assert.Contains("margen", row.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_UnknownFieldValue_ThrowsArgumentException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var request = Request(new[] { 1 }, new BulkPriceOperationDto
        {
            Field = (BulkPriceField)99,
            Mode = BulkPriceMode.SetFixed,
            Value = 1m
        });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.BulkUpdatePricesAsync(request));
        Assert.Contains("no reconocido", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_FilteredSelectionWithoutMatches_ReturnsEmptyResult()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        await service.CreateProductFromDtoAsync(new CreateProductDto { Name = "Cereal", SKU = "CER-01", CostPriceUSD = 1m, ProfitMarginRetail = 100m, PriceRetailUSD = 2m });

        var request = new BulkPriceUpdateRequestDto
        {
            SelectAllFiltered = true,
            Filter = new BulkPriceSelectionFilterDto { SearchText = "NoExiste" },
            Operations = new List<BulkPriceOperationDto> { Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 3m) }
        };

        var result = await service.PreviewBulkPriceUpdateAsync(request);

        Assert.Equal(0, result.SelectedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_WithoutSelectionMode_ThrowsArgumentException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var request = new BulkPriceUpdateRequestDto
        {
            Operations = new List<BulkPriceOperationDto> { Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1m) }
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.BulkUpdatePricesAsync(request));
        Assert.Contains("no especifica", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_WithBothSelectionModes_ThrowsArgumentException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var request = new BulkPriceUpdateRequestDto
        {
            ProductIds = new List<int> { 1 },
            SelectAllFiltered = true,
            Operations = new List<BulkPriceOperationDto> { Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1m) }
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.BulkUpdatePricesAsync(request));
        Assert.Contains("combinar", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_ExplicitSelectionAboveMax_ThrowsArgumentException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var request = new BulkPriceUpdateRequestDto
        {
            ProductIds = Enumerable.Range(1, 1001).ToList(),
            Operations = new List<BulkPriceOperationDto> { Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1m) }
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.BulkUpdatePricesAsync(request));
        Assert.Contains("excede", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_DuplicateFieldOperations_ThrowsArgumentException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var request = Request(new[] { 1 },
            Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1m),
            Op(BulkPriceField.RetailPrice, BulkPriceMode.IncreasePercent, 10m));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.BulkUpdatePricesAsync(request));
        Assert.Contains("una operación por campo", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_WithoutOperations_ThrowsArgumentException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var request = new BulkPriceUpdateRequestDto { ProductIds = new List<int> { 1 } };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewBulkPriceUpdateAsync(request));
        Assert.Contains("operaciones", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_NegativeOperationValue_ThrowsArgumentException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var request = Request(new[] { 1 }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, -1m));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.BulkUpdatePricesAsync(request));
        Assert.Contains("mayor o igual a cero", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_MissingExplicitProduct_ThrowsKeyNotFoundException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var request = Request(new[] { 9999 }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1m));

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.PreviewBulkPriceUpdateAsync(request));
        Assert.Contains("9999", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_WholesaleDisabledProduct_IsSkippedWithReason()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Sin Mayor",
            SKU = "SINMAY01",
            CostPriceUSD = 1m,
            ProfitMarginRetail = 100m,
            PriceRetailUSD = 2m
        });

        var result = await service.BulkUpdatePricesAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.WholesalePrice, BulkPriceMode.SetFixed, 5m)));

        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.SkippedCount);
        var row = Assert.Single(result.Rows);
        Assert.Contains("precio al mayor", row.SkipReason, StringComparison.OrdinalIgnoreCase);

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(2m, persisted.PriceRetailUSD);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_OperationWithoutEffectiveChange_IsSkippedAsNoChange()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Igual",
            SKU = "IGUAL01",
            CostPriceUSD = 1m,
            ProfitMarginRetail = 100m,
            PriceRetailUSD = 2m
        });

        var result = await service.BulkUpdatePricesAsync(
            Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.IncreasePercent, 0m)));

        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.SkippedCount);
        var row = Assert.Single(result.Rows);
        Assert.Contains("sin cambios", row.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_WithoutCatalogPermission_ThrowsUnauthorizedAccessException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context, canMutateCatalog: false);

        var request = Request(new[] { 1 }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1m));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewBulkPriceUpdateAsync(request));
    }
}
