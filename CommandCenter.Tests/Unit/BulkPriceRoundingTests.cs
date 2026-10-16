using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.182-T1 (S1-S5): integración del motor de redondeo comercial/psicológico en la edición masiva.
// Preview y apply comparten motor (consistencia 8.181); la salvaguarda Detal/Mayor bloquea el apply
// como fila IsError (all-or-nothing); las reglas ExactMath conservan el comportamiento 8.181.
public class BulkPriceRoundingTests
{
    private static InventoryService CreateService(InventoryDbContext context)
    {
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.CanMutateCatalog).Returns(true);
        user.Setup(u => u.UserId).Returns("admin-1");
        return new InventoryService(context, user.Object);
    }

    private static async Task SeedRateAsync(InventoryDbContext context, decimal rate)
    {
        context.ExchangeRateHistory.Add(new Core.Entities.ExchangeRateHistory
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
    // S1/S2/S5: psicológico USD — matemático, redondeado, delta y margen efectivo.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_UsdPsychological_ShowsMathRoundedDeltaAndEffectiveMargin_WithoutPersisting()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Psicologico",
            SKU = "PSI-01",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 20m,
            PriceRetailUSD = 1.20m
        });

        var request = Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1.37m));
        request.RetailRoundingRule = RoundingRuleType.UsdPsychological;

        var result = await service.PreviewBulkPriceUpdateAsync(request);

        Assert.Equal(1, result.UpdatedCount);
        var row = Assert.Single(result.Rows);
        Assert.Null(row.SkipReason);
        Assert.False(row.IsError);

        var change = ChangeOf(row, BulkPriceField.RetailPrice);
        Assert.Equal(1.20m, change.BeforeValue);
        Assert.Equal(1.25m, change.AfterValue); // 1.37: 12c al .25 vs 13c al .50
        Assert.Equal(1.37m, change.PreRoundingValue);
        Assert.Equal(-0.12m, change.RoundingDelta);
        Assert.Equal(120.00m, change.BeforeBsS);
        Assert.Equal(125.00m, change.AfterBsS);
        Assert.Equal(25.00m, row.EffectiveRetailMarginPct); // (1.25 - 1.00) / 1.00 x 100
        Assert.Null(row.EffectiveWholesaleMarginPct);

        // El preview no persiste nada.
        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(1.20m, persisted.PriceRetailUSD);
        Assert.Equal(120.00m, persisted.PriceBsS);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_UsdPsychological_PersistsRoundedPriceCanonicalBsSAndKeepsOperatedMargin()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Psicologico",
            SKU = "PSI-01",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 20m,
            PriceRetailUSD = 1.20m
        });

        var request = Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1.37m));
        request.RetailRoundingRule = RoundingRuleType.UsdPsychological;

        var result = await service.BulkUpdatePricesAsync(request);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(25.00m, Assert.Single(result.Rows).EffectiveRetailMarginPct); // informativo (precio final)

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(1.25m, persisted.PriceRetailUSD);
        Assert.Equal(1.25m, persisted.PriceUSD);
        Assert.Equal(125.00m, persisted.PriceBsS); // Bs.S canónico desde el USD final
        // 8.183 (R3): la regla movió el precio pero NUNCA pisa el margen operado (20): el margen
        // efectivo (25) queda solo en la fila informativa del preview.
        Assert.Equal(20.00m, persisted.ProfitMarginRetail);
        Assert.Equal(20.00m, persisted.ProfitPercentage);
    }

    // ---------------------------------------------------------------------------------------------
    // S4: VES redondea el Bs.S canónico del canal; USD y margen no cambian.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewAndApplyBulkPriceUpdateAsync_VesCashFriendly_ProjectsCashFriendlyBsSWithoutPersistingIt()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 842.21m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Efectivo",
            SKU = "EFE-01",
            CostPriceUSD = 0.60m,
            ProfitMarginRetail = 35m,
            PriceRetailUSD = 0.81m
        });

        var request = Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 0.81m));
        request.RetailRoundingRule = RoundingRuleType.VesCashFriendlyStep5;

        var preview = await service.PreviewBulkPriceUpdateAsync(request);

        // 8.183 (R1): la regla VES ya no persiste NADA — sin cambio de USD no hay cambio accionable
        // (la divergencia 8.182 mutaba el Bs.S de productos que la operación no tocó).
        Assert.Equal(0, preview.UpdatedCount);
        var row = Assert.Single(preview.Rows);
        Assert.Contains("sin cambios", row.SkipReason, StringComparison.OrdinalIgnoreCase);
        var change = ChangeOf(row, BulkPriceField.RetailPrice);
        Assert.Equal(0.81m, change.BeforeValue);
        Assert.Equal(0.81m, change.AfterValue); // el USD no cambia
        Assert.Equal(682.20m, change.BeforeBsS); // Bs.S canónico almacenado (8.104)
        Assert.Equal(682.20m, change.AfterBsS);  // el canónico persistido no se muta
        Assert.Equal(680.00m, change.CashFriendlyAfterBsS); // proyección: 0.81 x 842.21 -> paso 5: 680
        Assert.Equal(682.1901m, change.PreRoundingValue);
        Assert.Equal(-2.1901m, change.RoundingDelta);
        Assert.Null(row.EffectiveRetailMarginPct); // el precio USD no cambió

        var untouched = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(682.20m, untouched.PriceBsS);

        var applied = await service.BulkUpdatePricesAsync(request);
        Assert.Equal(0, applied.UpdatedCount);
        var appliedChange = ChangeOf(RowOf(applied, created.Id), BulkPriceField.RetailPrice);
        Assert.Equal(680.00m, appliedChange.CashFriendlyAfterBsS); // proyección estable, jamás persistida
        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(0.81m, persisted.PriceRetailUSD);
        Assert.Equal(0.81m, persisted.PriceUSD);
        Assert.Equal(682.20m, persisted.PriceBsS); // canónico 8.104, no el amigable proyectado
        // S4: VES solo proyecta → el margen operado queda intacto.
        Assert.Equal(35m, persisted.ProfitMarginRetail);
        Assert.Equal(35m, persisted.ProfitPercentage);
    }

    // ---------------------------------------------------------------------------------------------
    // S2: piso de margen por lote fuerza el escalón superior.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_MarginFloor_ForcesNextStepUpAndShowsEffectiveMargin()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Piso",
            SKU = "PISO01",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 20m,
            PriceRetailUSD = 1.20m
        });

        var request = Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1.37m));
        request.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        request.EnforceMinimumMarginFloor = true;
        request.RetailMarginFloorPct = 30m;

        var result = await service.PreviewBulkPriceUpdateAsync(request);

        var row = Assert.Single(result.Rows);
        var change = ChangeOf(row, BulkPriceField.RetailPrice);
        Assert.Equal(1.50m, change.AfterValue); // 1.25 (25%) rechazado por el piso 30% -> 1.50
        Assert.Equal(1.37m, change.PreRoundingValue);
        Assert.Equal(0.13m, change.RoundingDelta);
        Assert.Equal(150.00m, change.AfterBsS);
        Assert.Equal(50.00m, row.EffectiveRetailMarginPct);
    }

    // ---------------------------------------------------------------------------------------------
    // S3: salvaguarda Detal/Mayor — ajuste automático y caso imposible.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewAndApplyBulkPriceUpdateAsync_WholesaleRoundingReachesRetail_AutoLowersWholesaleToLowerStep()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Con Mayor",
            SKU = "MAY-01",
            CostPriceUSD = 0.60m,
            ProfitMarginRetail = 100m,
            ProfitMarginWholesale = 80m,
            PriceRetailUSD = 1.60m,
            PriceWholesaleUSD = 1.30m,
            HasWholesale = true
        });

        var request = Request(new[] { created.Id },
            Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1.37m),
            Op(BulkPriceField.WholesalePrice, BulkPriceMode.SetFixed, 1.30m));
        request.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        request.WholesaleRoundingRule = RoundingRuleType.UsdPsychological;

        var preview = await service.PreviewBulkPriceUpdateAsync(request);

        var row = Assert.Single(preview.Rows);
        Assert.Null(row.SkipReason);
        Assert.False(row.IsError);
        var retailChange = ChangeOf(row, BulkPriceField.RetailPrice);
        Assert.Equal(1.25m, retailChange.AfterValue);
        var wholesaleChange = ChangeOf(row, BulkPriceField.WholesalePrice);
        Assert.Equal(1.30m, wholesaleChange.BeforeValue);
        Assert.Equal(0.99m, wholesaleChange.AfterValue); // 1.30 -> 1.25 >= detal 1.25 -> escalón inferior
        Assert.Equal(1.30m, wholesaleChange.PreRoundingValue);
        Assert.Equal(-0.31m, wholesaleChange.RoundingDelta);
        Assert.Equal(108.33m, row.EffectiveRetailMarginPct); // (1.25 - 0.60) / 0.60 x 100
        Assert.Equal(65.00m, row.EffectiveWholesaleMarginPct); // (0.99 - 0.60) / 0.60 x 100

        await service.BulkUpdatePricesAsync(request);
        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(1.25m, persisted.PriceRetailUSD);
        Assert.Equal(0.99m, persisted.PriceWholesaleUSD);
        Assert.Equal(125.00m, persisted.PriceBsS);
        // 8.183 (R3): la regla y la salvaguarda solo mueven precios — los márgenes persistidos son
        // los operados originales (100 / 80); el efectivo (108.33 / 65.00) queda solo en la fila.
        Assert.Equal(100.00m, persisted.ProfitMarginRetail);
        Assert.Equal(80.00m, persisted.ProfitMarginWholesale);
        Assert.Equal(100.00m, persisted.ProfitPercentage);
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_SalvageImpossible_IsErrorAndApplyThrowsWithoutWrites()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Sin Salvacion",
            SKU = "SALV01",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 100m,
            ProfitMarginWholesale = 80m,
            PriceRetailUSD = 1.60m,
            PriceWholesaleUSD = 1.30m,
            HasWholesale = true
        });

        var request = Request(new[] { created.Id },
            Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1.37m),
            Op(BulkPriceField.WholesalePrice, BulkPriceMode.SetFixed, 1.30m));
        request.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        request.WholesaleRoundingRule = RoundingRuleType.UsdPsychological;

        var preview = await service.PreviewBulkPriceUpdateAsync(request);
        var row = Assert.Single(preview.Rows);
        Assert.True(row.IsError);
        Assert.Contains("escalón", row.SkipReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("costo", row.SkipReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(row.Changes);

        await Assert.ThrowsAsync<ArgumentException>(() => service.BulkUpdatePricesAsync(request));

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(1.60m, persisted.PriceRetailUSD);
        Assert.Equal(1.30m, persisted.PriceWholesaleUSD);
    }

    // ---------------------------------------------------------------------------------------------
    // S4: VES en el canal al mayor (Bs.S informativo; sin columna propia en la entidad).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_WholesaleVes_ReportsInformationalWholesaleBsS()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Mayor Efectivo",
            SKU = "MAYE01",
            CostPriceUSD = 2.00m,
            ProfitMarginRetail = 100m,
            ProfitMarginWholesale = 80m,
            PriceRetailUSD = 10.00m,
            PriceWholesaleUSD = 8.00m,
            HasWholesale = true
        });

        var request = Request(new[] { created.Id }, Op(BulkPriceField.WholesalePrice, BulkPriceMode.SetFixed, 9.03m));
        request.WholesaleRoundingRule = RoundingRuleType.VesCashFriendlyStep5;

        var result = await service.PreviewBulkPriceUpdateAsync(request);

        var row = Assert.Single(result.Rows);
        var change = Assert.Single(row.Changes);
        Assert.Equal(BulkPriceField.WholesalePrice, change.Field);
        Assert.Equal(9.03m, change.AfterValue);
        Assert.Equal(800.00m, change.BeforeBsS);
        Assert.Equal(905.00m, change.AfterBsS); // 903 -> paso 5: 905
        // 8.183 (R1): el Bs.S equivalente del mayor ES su proyección de efectivo (nunca persistida).
        Assert.Equal(905.00m, change.CashFriendlyAfterBsS);
        Assert.Equal(903m, change.PreRoundingValue);
        Assert.Equal(2.00m, change.RoundingDelta);
        Assert.Equal(351.50m, row.EffectiveWholesaleMarginPct); // (9.03 - 2.00) / 2.00 x 100
        Assert.Null(row.EffectiveRetailMarginPct);

        // La regla VES al mayor no movió el precio USD (solo el Bs.S informativo): margen intacto.
        await service.BulkUpdatePricesAsync(request);
        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(9.03m, persisted.PriceWholesaleUSD);
        Assert.Equal(80m, persisted.ProfitMarginWholesale);
    }

    // ---------------------------------------------------------------------------------------------
    // S4: consistencia preview == apply con reglas activas (incl. campos 8.182).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BulkUpdatePricesAsync_PreviewAndApplyShareTheSameEngineWithRoundingRules_ResultsMatchExactly()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);

        var simple = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Simple",
            SKU = "SIMPLE1",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 20m,
            PriceRetailUSD = 1.20m
        });
        var withWholesale = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Mayorista",
            SKU = "MAYOR1",
            CostPriceUSD = 0.60m,
            ProfitMarginRetail = 100m,
            ProfitMarginWholesale = 80m,
            PriceRetailUSD = 1.60m,
            PriceWholesaleUSD = 1.30m,
            HasWholesale = true
        });

        var request = Request(new[] { simple.Id, withWholesale.Id },
            Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1.37m));
        request.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        request.WholesaleRoundingRule = RoundingRuleType.UsdPsychological;

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
            Assert.Equal(previewRow.EffectiveRetailMarginPct, appliedRow.EffectiveRetailMarginPct);
            Assert.Equal(previewRow.EffectiveWholesaleMarginPct, appliedRow.EffectiveWholesaleMarginPct);
            Assert.Equal(
                previewRow.Changes.Select(c => (c.Field, c.BeforeValue, c.AfterValue, c.BeforeBsS, c.AfterBsS, c.PreRoundingValue, c.RoundingDelta, c.CashFriendlyAfterBsS)),
                appliedRow.Changes.Select(c => (c.Field, c.BeforeValue, c.AfterValue, c.BeforeBsS, c.AfterBsS, c.PreRoundingValue, c.RoundingDelta, c.CashFriendlyAfterBsS)));
        }

        foreach (var row in preview.Rows.Where(r => r.SkipReason == null))
        {
            var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == row.ProductId);
            var retailChange = row.Changes.Single(c => c.Field == BulkPriceField.RetailPrice);
            Assert.Equal(retailChange.AfterValue, persisted.PriceRetailUSD);
            Assert.Equal(retailChange.AfterBsS, persisted.PriceBsS);
            // 8.183 (R3): el margen persistido es el operado (20 / 100), nunca el efectivo que la
            // fila informa (25 / 108.33): el redondeo no contamina la fuente del margen.
            var operatedRetailMargin = row.ProductId == simple.Id ? 20m : 100m;
            Assert.Equal(operatedRetailMargin, persisted.ProfitMarginRetail);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // S4/S5: sin efecto cuando la regla no altera el valor; ExactMath conserva 8.181.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task BulkUpdatePricesAsync_PsychologicalRuleOverPriceAlreadyOnTermination_IsSkippedAsNoChange()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Ya Psico",
            SKU = "YAPS01",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 25m,
            PriceRetailUSD = 1.25m
        });

        var request = Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1.25m));
        request.RetailRoundingRule = RoundingRuleType.UsdPsychological;

        var result = await service.BulkUpdatePricesAsync(request);

        Assert.Equal(0, result.UpdatedCount);
        var row = Assert.Single(result.Rows);
        Assert.Contains("sin cambios", row.SkipReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(row.Changes);
        Assert.Null(row.EffectiveRetailMarginPct);

        // Regla sin efecto: el margen operado queda intacto (8.181).
        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(1.25m, persisted.PriceRetailUSD);
        Assert.Equal(25m, persisted.ProfitMarginRetail);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_RoundingRuleMovedPrice_KeepsOperatedMarginIntact()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Override",
            SKU = "OVR-01",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 20m,
            PriceRetailUSD = 1.20m
        });

        var request = Request(new[] { created.Id },
            Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1.37m),
            Op(BulkPriceField.RetailMargin, BulkPriceMode.SetFixed, 30m));
        request.RetailRoundingRule = RoundingRuleType.UsdPsychological;

        var result = await service.BulkUpdatePricesAsync(request);

        var row = Assert.Single(result.Rows);
        Assert.Equal(25.00m, row.EffectiveRetailMarginPct); // informativo (precio final vs costo)
        Assert.Equal(30.00m, ChangeOf(row, BulkPriceField.RetailMargin).AfterValue); // objetivo operado

        // 8.183 (R3): el margen operado (30) se persiste tal cual; el efectivo (25) NO lo pisa.
        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(1.25m, persisted.PriceRetailUSD);
        Assert.Equal(30.00m, persisted.ProfitMarginRetail);
        Assert.Equal(30.00m, persisted.ProfitPercentage);
    }

    [Fact]
    public async Task BulkUpdatePricesAsync_RoundingRuleWithoutEffect_KeepsOperatedMarginIntact()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        await SeedRateAsync(context, 100m);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Sin Efecto",
            SKU = "SINEF1",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 20m,
            PriceRetailUSD = 1.20m
        });

        // 1.50 ya es una terminación psicológica: el cambio 1.20 -> 1.50 es de la operación, no de la regla.
        var request = Request(new[] { created.Id }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1.50m));
        request.RetailRoundingRule = RoundingRuleType.UsdPsychological;

        var result = await service.BulkUpdatePricesAsync(request);

        var row = Assert.Single(result.Rows);
        Assert.Equal(50.00m, row.EffectiveRetailMarginPct); // informativo S5 del precio final

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(1.50m, persisted.PriceRetailUSD);
        Assert.Equal(20m, persisted.ProfitMarginRetail); // margen operado intacto (regla sin efecto)
    }

    // ---------------------------------------------------------------------------------------------
    // 8.183 (R2/R3): red de regresión del Efecto Trinquete — 10 ejecuciones consecutivas.
    // ---------------------------------------------------------------------------------------------

    // Por qué esta prueba es la red del trinquete: en 8.182 cada apply pisaba el margen persistido
    // con el EFECTIVO inflado por el redondeo (y persistía el Bs.S amigable en las reglas VES).
    // La siguiente iteración partía de ese margen contaminado y lo acumulaba. Con el ancla correcta
    // (costo × (1 + margen objetivo) sin redondear), el margen persistido siempre objetivo y el
    // Bs.S almacenado siempre canónico 8.104, diez ejecuciones del mismo apply deben producir
    // exactamente el mismo precio USD, el mismo Bs.S, el margen objetivo intacto y una proyección
    // de efectivo (VES) estable que NUNCA se persiste.
    // Casos del trinquete: regla por canal, costo, precio sembrado (margen 20 de arranque), precio
    // USD final esperado (margen objetivo 30 re-aplicado) y proyección de efectivo esperada (null
    // para reglas USD: solo las reglas VES proyectan Bs.S amigable). El psicológico cubre los
    // aterrizajes .99 / .95 / .50 (matemáticos con techo 0.98 / 1.89 / 0.59) y los VES, múltiplos
    // de 1 / 5 / 10 Bs.S.
    public static IEnumerable<object[]> RatchetRoundingCases()
    {
        yield return new object[] { RoundingRuleType.UsdPsychological, 0.75m, 0.90m, 0.99m, null! };
        yield return new object[] { RoundingRuleType.UsdPsychological, 1.45m, 1.74m, 1.95m, null! };
        yield return new object[] { RoundingRuleType.UsdPsychological, 0.45m, 0.54m, 0.50m, null! };
        yield return new object[] { RoundingRuleType.VesCashFriendlyInteger, 0.75m, 0.90m, 0.98m, 825m };
        yield return new object[] { RoundingRuleType.VesCashFriendlyStep5, 0.75m, 0.90m, 0.98m, 825m };
        yield return new object[] { RoundingRuleType.VesCashFriendlyStep10, 0.75m, 0.90m, 0.98m, 830m };
    }

    [Theory]
    [MemberData(nameof(RatchetRoundingCases))]
    public async Task ApplyRounding_ExecutedTenConsecutiveTimes_ShouldRemainIdempotentAndNotInflatePrice(
        RoundingRuleType rule,
        decimal costUsd,
        decimal seedPriceUsd,
        decimal expectedRetailUsd,
        decimal? expectedCashFriendlyBsS)
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        const decimal rate = 842.21m;
        await SeedRateAsync(context, rate);
        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = $"Idempotencia {rule}",
            SKU = $"IDEM-{rule}",
            CostPriceUSD = costUsd,
            ProfitMarginRetail = 20m,
            PriceRetailUSD = seedPriceUsd
        });

        // El margen objetivo (30%) se re-aplica en cada iteración; el precio debe anclarse SIEMPRE en
        // costo × (1 + 30/100) con techo y luego en la escalera de la regla del canal.
        var request = Request(new[] { created.Id }, Op(BulkPriceField.RetailMargin, BulkPriceMode.SetFixed, 30m));
        request.RetailRoundingRule = rule;

        var usdByIteration = new List<decimal>();
        var bsSByIteration = new List<decimal>();
        var projectionByIteration = new List<decimal?>();

        for (var iteration = 1; iteration <= 10; iteration++)
        {
            var result = await service.BulkUpdatePricesAsync(request);
            var row = RowOf(result, created.Id);

            if (iteration == 1)
            {
                Assert.Equal(1, result.UpdatedCount);
                Assert.Null(row.SkipReason);
            }
            else
            {
                // Desde la segunda ejecución el estado ya está en el objetivo: nada que persistir.
                Assert.Equal(0, result.UpdatedCount);
                Assert.Contains("sin cambios", row.SkipReason, StringComparison.OrdinalIgnoreCase);
            }

            var retailChange = row.Changes.SingleOrDefault(change => change.Field == BulkPriceField.RetailPrice);
            if (iteration == 1 || expectedCashFriendlyBsS.HasValue)
            {
                // La proyección VES viaja como campo informativo del cambio aunque el estado persistido
                // ya no cambie (nunca participa de HasChanges/UpdatedCount).
                Assert.NotNull(retailChange);
            }

            projectionByIteration.Add(retailChange?.CashFriendlyAfterBsS);

            var persisted = await context.Products.AsNoTracking().SingleAsync(product => product.Id == created.Id);
            usdByIteration.Add(persisted.PriceRetailUSD);
            bsSByIteration.Add(persisted.PriceBsS);
        }

        // R2: diez ejecuciones del mismo apply producen el MISMO precio USD (sin inflación acumulativa).
        Assert.Equal(usdByIteration[0], usdByIteration[9]);
        Assert.All(usdByIteration, usd => Assert.Equal(expectedRetailUsd, usd));

        // R3: el margen persistido es SIEMPRE el objetivo/operado (30), jamás el efectivo del redondeo.
        var finalProduct = await context.Products.AsNoTracking().SingleAsync(product => product.Id == created.Id);
        Assert.Equal(30m, finalProduct.ProfitMarginRetail);
        Assert.Equal(30m, finalProduct.ProfitPercentage);

        // R1: el Bs.S almacenado es SIEMPRE el canónico 8.104 (ceil) y no diverge entre iteraciones.
        Assert.All(bsSByIteration, bsS => Assert.Equal(PricingCalculator.ToBsSCeiling(expectedRetailUsd, rate), bsS));

        // R1/R4: la proyección de efectivo (VES) permanece estable y NUNCA se persiste.
        Assert.All(projectionByIteration, projection => Assert.Equal(expectedCashFriendlyBsS, projection));
    }

    // ---------------------------------------------------------------------------------------------
    // S5: validación del contrato (400) de los campos nuevos.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_UnknownRoundingRule_ThrowsArgumentException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var request = Request(new[] { 1 }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1m));
        request.RetailRoundingRule = (RoundingRuleType)99;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewBulkPriceUpdateAsync(request));
        Assert.Contains("no reconocida", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewBulkPriceUpdateAsync_NegativeMarginFloor_ThrowsArgumentException()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var request = Request(new[] { 1 }, Op(BulkPriceField.RetailPrice, BulkPriceMode.SetFixed, 1m));
        request.EnforceMinimumMarginFloor = true;
        request.RetailMarginFloorPct = -1m;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewBulkPriceUpdateAsync(request));
        Assert.Contains("negativo", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
