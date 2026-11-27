using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Constants;
using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.186-T7c (S4/S1): la edición de ítems de una cuenta abierta (UpdateSaleItemsAsync) debe
/// recrear cada línea con la MISMA regla cash efectiva (override del artículo ?? default global)
/// y el MISMO Bs.S efectivo que resolvía AddItemAsync (kernel único con el toggle de piso), para
/// que la recalculación posterior no degrade la línea a la semántica canónica (preview ==
/// persistido). Cobertura: (a) cambio de cantidad e ítem agregado con Step5/Step10, (b) línea cash
/// preexistente que no se degrada, (c) regresión ExactMath/default byte-idéntica, (d) precio
/// custom autorizado con semántica canónica, (e) herencia del default global.
/// </summary>
public class HoldOrderRoundingPersistenceTests
{
    private const int TestActorId = 42;
    private const decimal Rate = 842.21m;

    private static Sale BuildClaimedOnHoldSale(decimal appliedRate) => new()
    {
        Id = 1,
        Status = SaleStatus.OnHold,
        AppliedRate = appliedRate,
        ClaimedByUserId = TestActorId,
        ClaimAction = SaleClaimAction.Editing,
        ClaimedByUserName = "Test Actor",
        ClaimedAtUtc = DateTime.UtcNow
    };

    private static SaleProductInfoDto BuildProduct(int id, string name, decimal priceUsd, RoundingRuleType? rule) => new()
    {
        Id = id,
        Name = name,
        IsActive = true,
        IsDeleted = false,
        PriceUSD = priceUsd,
        PriceRetailUSD = priceUsd,
        PriceWholesaleUSD = priceUsd,
        PriceBsS = PricingCalculator.ToBsSCeiling(priceUsd, Rate),
        MinWholesaleQuantity = 6m,
        IsFractional = false,
        UnitOfMeasure = Core.Entities.UnitOfMeasureType.Und,
        CostPriceUSD = priceUsd * 0.5m,
        RoundingRuleCashBs = rule
    };

    private static SalesService CreateService(
        SalesDbContext context,
        IReadOnlyList<SaleProductInfoDto> products,
        string? globalCashRule,
        string? enforceFloorSetting,
        IMonetaryProfileService? monetaryProfileService = null,
        ICurrencyRateService? currencyRateService = null)
    {
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(products.ToList());
        foreach (var product in products)
        {
            var captured = product;
            inventory.Setup(i => i.GetSaleProductByIdAsync(captured.Id, It.IsAny<CancellationToken>())).ReturnsAsync(captured);
        }

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultCashBsRoundingRule)).ReturnsAsync(globalCashRule);
        settings.Setup(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor)).ReturnsAsync(enforceFloorSetting);

        return new SalesService(context, inventory.Object, Mock.Of<IMediator>(), new CashDrawerService(context), settings.Object, monetaryProfileService: monetaryProfileService, currencyRateService: currencyRateService);
    }

    private static async Task<Sale> ReloadSaleAsync(string dbName)
    {
        await using var verifyContext = TestDatabaseFactory.CreateSalesDbContext(dbName);
        return await verifyContext.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .FirstAsync(s => s.Id == 1);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_ManualPair_UsesEightDecimalCalculationRateAndFourDecimalSnapshot()
    {
        var dbName = $"hold_rounding_u4_manual_{Guid.NewGuid():N}";
        using var context = TestDatabaseFactory.CreateSalesDbContext(dbName);

        var product = BuildProduct(603, "Producto Manual EUR", 1000m, rule: null);
        var sale = BuildClaimedOnHoldSale(0.9245m);
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var profile = new Mock<IMonetaryProfileService>();
        profile.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = "EUR",
            SecondaryCurrencySymbol = "EUR"
        });
        var rates = new Mock<ICurrencyRateService>();
        rates.Setup(r => r.GetRateAsync("USD", "EUR", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(
                0.92451234m,
                CurrencyRateSource.Manual,
                DateOnly.FromDateTime(DateTime.UtcNow),
                "USD",
                "EUR"));

        var service = CreateService(
            context,
            new[] { product },
            globalCashRule: null,
            enforceFloorSetting: null,
            monetaryProfileService: profile.Object,
            currencyRateService: rates.Object);

        var updated = await service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            ExchangeRate = 0.92451234m,
            Items = new List<UpdateSaleItemDto> { new() { ProductId = 603, Quantity = 1m, UnitPrice = 1000m } }
        }, actingUserId: TestActorId);

        Assert.Equal(0.9245m, updated.AppliedRate);
        Assert.Equal(924.52m, Assert.Single(updated.Items).UnitPriceBsS);
        Assert.Equal(924.52m, updated.TotalBsS);

        var persisted = await ReloadSaleAsync(dbName);
        Assert.Equal(0.9245m, persisted.AppliedRate);
        Assert.Equal(924.52m, Assert.Single(persisted.Items).UnitPriceBsS);
        Assert.Equal(924.52m, persisted.TotalBsS);
    }
    [Fact]
    public async Task UpdateSaleItemsAsync_ManualPair_WithoutRequestRate_ResolvesEightDecimalQuote()
    {
        var dbName = $"hold_rounding_u4_manual_omitted_{Guid.NewGuid():N}";
        using var context = TestDatabaseFactory.CreateSalesDbContext(dbName);

        var product = BuildProduct(604, "Producto Manual EUR sin tasa", 1000m, rule: null);
        var sale = BuildClaimedOnHoldSale(0.9245m);
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var profile = new Mock<IMonetaryProfileService>();
        profile.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = "EUR",
            SecondaryCurrencySymbol = "EUR"
        });
        var rates = new Mock<ICurrencyRateService>();
        rates.Setup(r => r.GetRateAsync("USD", "EUR", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(
                0.92451234m,
                CurrencyRateSource.Manual,
                DateOnly.FromDateTime(DateTime.UtcNow),
                "USD",
                "EUR"));

        var service = CreateService(
            context,
            new[] { product },
            globalCashRule: null,
            enforceFloorSetting: null,
            monetaryProfileService: profile.Object,
            currencyRateService: rates.Object);

        var updated = await service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto> { new() { ProductId = 604, Quantity = 1m, UnitPrice = 1000m } }
        }, actingUserId: TestActorId);

        Assert.Equal(0.9245m, updated.AppliedRate);
        Assert.Equal(924.52m, Assert.Single(updated.Items).UnitPriceBsS);
        Assert.Equal(924.52m, updated.TotalBsS);
    }
    [Fact]
    public async Task UpdateSaleItemsAsync_QuantityChange_KeepsStep10SnapshotAndEffectiveBsSTotal()
    {
        var dbName = $"hold_rounding_t7c_qty_{Guid.NewGuid():N}";
        using var context = TestDatabaseFactory.CreateSalesDbContext(dbName);

        var product = BuildProduct(601, "Producto Step10", 1.30m, RoundingRuleType.VesCashFriendlyStep10);
        var sale = BuildClaimedOnHoldSale(Rate);
        sale.Items.Add(new SaleItem
        {
            ProductId = 601,
            ProductName = product.Name,
            Quantity = 2m,
            UnitPrice = 1.30m,
            UnitPriceBsS = 1100.00m,
            Subtotal = 2.60m,
            SubtotalBsS = 2200.00m,
            RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep10
        });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, new[] { product }, globalCashRule: null, enforceFloorSetting: "True");

        var updated = await service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto> { new() { ProductId = 601, Quantity = 3m, UnitPrice = 1.30m } }
        }, actingUserId: TestActorId);

        // Línea recreada: 1.30 × 842.21 = 1094.873 → Step10 (nunca hacia abajo) = 1100.00 × 3 = 3300.00.
        var updatedItem = Assert.Single(updated.Items);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, updatedItem.RoundingRuleCashBs);
        Assert.Equal(1100.00m, updatedItem.UnitPriceBsS);
        Assert.Equal(3300.00m, updated.TotalBsS);

        var persisted = await ReloadSaleAsync(dbName);
        var item = Assert.Single(persisted.Items);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, item.RoundingRuleCashBs);
        Assert.Equal(1100.00m, item.UnitPriceBsS);
        Assert.Equal(3300.00m, item.SubtotalBsS);
        Assert.Equal(3.90m, persisted.TotalUSD);
        Assert.Equal(3300.00m, persisted.TotalBsS);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_ItemAdded_ResolvesProductOverrideRuleAndKeepsMixedRulesCoherent()
    {
        var dbName = $"hold_rounding_t7c_add_{Guid.NewGuid():N}";
        using var context = TestDatabaseFactory.CreateSalesDbContext(dbName);

        var existing = BuildProduct(602, "Producto ExactMath", 2.00m, rule: null);
        var added = BuildProduct(601, "Producto Step5", 1.30m, RoundingRuleType.VesCashFriendlyStep5);

        var sale = BuildClaimedOnHoldSale(Rate);
        sale.Items.Add(new SaleItem
        {
            ProductId = 602,
            ProductName = existing.Name,
            Quantity = 1m,
            UnitPrice = 2.00m,
            UnitPriceBsS = 1684.42m,
            Subtotal = 2.00m,
            SubtotalBsS = 1684.42m,
            RoundingRuleCashBs = RoundingRuleType.ExactMath
        });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, new[] { existing, added }, globalCashRule: null, enforceFloorSetting: "True");

        await service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto>
            {
                new() { ProductId = 602, Quantity = 1m, UnitPrice = 2.00m },
                new() { ProductId = 601, Quantity = 1m, UnitPrice = 1.30m }
            }
        }, actingUserId: TestActorId);

        var persisted = await ReloadSaleAsync(dbName);

        // Línea agregada: override Step5 del artículo → 1094.873 → 1095.00 (no canónico 1094.88).
        var addedLine = persisted.Items.Single(i => i.ProductId == 601);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, addedLine.RoundingRuleCashBs);
        Assert.Equal(1095.00m, addedLine.UnitPriceBsS);
        Assert.Equal(1095.00m, addedLine.SubtotalBsS);

        // Línea preexistente sin política: ExactMath ⇒ techo canónico byte-idéntico.
        var existingLine = persisted.Items.Single(i => i.ProductId == 602);
        Assert.Equal(RoundingRuleType.ExactMath, existingLine.RoundingRuleCashBs);
        Assert.Equal(1684.42m, existingLine.UnitPriceBsS);

        Assert.Equal(3.30m, persisted.TotalUSD);
        Assert.Equal(2779.42m, persisted.TotalBsS);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_WithoutAnyCashRule_KeepsCanonicalTotalsUnchanged()
    {
        var dbName = $"hold_rounding_t7c_exact_{Guid.NewGuid():N}";
        using var context = TestDatabaseFactory.CreateSalesDbContext(dbName);

        var product = BuildProduct(602, "Producto Sin Regla", 1.20m, rule: null);
        var sale = BuildClaimedOnHoldSale(50.00m);
        sale.Items.Add(new SaleItem
        {
            ProductId = 602,
            ProductName = product.Name,
            Quantity = 2m,
            UnitPrice = 1.20m,
            UnitPriceBsS = 60.00m,
            Subtotal = 2.40m,
            SubtotalBsS = 120.00m,
            RoundingRuleCashBs = RoundingRuleType.ExactMath
        });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, new[] { product }, globalCashRule: null, enforceFloorSetting: null);

        await service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto> { new() { ProductId = 602, Quantity = 3m, UnitPrice = 1.20m } }
        }, actingUserId: TestActorId);

        var persisted = await ReloadSaleAsync(dbName);
        var item = Assert.Single(persisted.Items);

        Assert.Equal(RoundingRuleType.ExactMath, item.RoundingRuleCashBs);
        Assert.Equal(60.00m, item.UnitPriceBsS);
        Assert.Equal(180.00m, item.SubtotalBsS);
        Assert.Equal(3.60m, persisted.TotalUSD);
        Assert.Equal(180.00m, persisted.TotalBsS);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_CustomAuthorizedPrice_UsesCanonicalCeilingAndKeepsCustomSemantics()
    {
        var dbName = $"hold_rounding_t7c_custom_{Guid.NewGuid():N}";
        using var context = TestDatabaseFactory.CreateSalesDbContext(dbName);

        var product = BuildProduct(601, "Producto Step10", 1.30m, RoundingRuleType.VesCashFriendlyStep10);
        var sale = BuildClaimedOnHoldSale(Rate);
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, new[] { product }, globalCashRule: null, enforceFloorSetting: "True");

        await service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto> { new() { ProductId = 601, Quantity = 2m, UnitPrice = 1.40m } }
        }, isPriceOverrideAuthorized: true, actingUserId: TestActorId);

        var persisted = await ReloadSaleAsync(dbName);
        var item = Assert.Single(persisted.Items);

        // El snapshot de la regla viaja (espejo de AddItemAsync) pero el precio manual no participa
        // de la escalera cash: 1.40 × 842.21 = 1179.094 → techo canónico 1179.10 (Step10 daría 1180.00).
        Assert.True(item.IsCustomPrice);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, item.RoundingRuleCashBs);
        Assert.Equal(1.40m, item.UnitPrice);
        Assert.Equal(1179.10m, item.UnitPriceBsS);
        Assert.Equal(2358.20m, item.SubtotalBsS);
        Assert.Equal(2.80m, persisted.TotalUSD);
        Assert.Equal(2358.20m, persisted.TotalBsS);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_ProductWithoutOverride_InheritsGlobalCashRule()
    {
        var dbName = $"hold_rounding_t7c_global_{Guid.NewGuid():N}";
        using var context = TestDatabaseFactory.CreateSalesDbContext(dbName);

        var product = BuildProduct(601, "Producto Global Step10", 1.30m, rule: null);
        var sale = BuildClaimedOnHoldSale(Rate);
        sale.Items.Add(new SaleItem
        {
            ProductId = 601,
            ProductName = product.Name,
            Quantity = 1m,
            UnitPrice = 1.30m,
            UnitPriceBsS = 1094.88m,
            Subtotal = 1.30m,
            SubtotalBsS = 1094.88m
        });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, new[] { product }, globalCashRule: "VesCashFriendlyStep10", enforceFloorSetting: "True");

        await service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto> { new() { ProductId = 601, Quantity = 2m, UnitPrice = 1.30m } }
        }, actingUserId: TestActorId);

        var persisted = await ReloadSaleAsync(dbName);
        var item = Assert.Single(persisted.Items);

        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, item.RoundingRuleCashBs);
        Assert.Equal(1100.00m, item.UnitPriceBsS);
        Assert.Equal(2200.00m, item.SubtotalBsS);
        Assert.Equal(2.60m, persisted.TotalUSD);
        Assert.Equal(2200.00m, persisted.TotalBsS);
    }
}
