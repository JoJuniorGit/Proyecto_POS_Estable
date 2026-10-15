using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.184-T2 (S5): persistencia nullable de las reglas de redondeo por artículo (Product).
/// Semántica: null = hereda la política global; el update reemplaza por completo (null explícito
/// del selector "Heredar global" limpia el override). Whitelist zero-trust con Enum.IsDefined:
/// un valor fuera del catálogo no persiste y el flujo responde 400 vía ArgumentException.
/// </summary>
public class ProductRoundingPersistenceTests
{
    private static InventoryService CreateService(InventoryDbContext context)
    {
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.CanMutateCatalog).Returns(true);
        user.Setup(u => u.UserId).Returns("rounding-persistence-tests");
        return new InventoryService(context, user.Object);
    }

    private static CreateProductDto CreateDto(string sku = "RND-01") => new()
    {
        Name = "Producto redondeo",
        SKU = sku,
        CostPriceUSD = 1.00m,
        ProfitMarginRetail = 20m,
        PriceRetailUSD = 1.20m
    };

    private static UpdateProductDto ToUpdateDto(ProductDto product) => new()
    {
        Id = product.Id,
        Name = product.Name,
        SKU = product.SKU,
        Description = product.Description,
        PriceUSD = product.PriceUSD,
        PriceRetailUSD = product.PriceRetailUSD,
        PriceWholesaleUSD = product.PriceWholesaleUSD,
        CostPriceUSD = product.CostPriceUSD,
        ProfitMarginRetail = product.ProfitMarginRetail,
        ProfitMarginWholesale = product.ProfitMarginWholesale,
        MinWholesaleQuantity = product.MinWholesaleQuantity,
        HasWholesale = product.HasWholesale,
        IsFractional = product.IsFractional,
        UnitOfMeasure = product.UnitOfMeasure,
        LowStockThreshold = product.LowStockThreshold,
        IsCashAdvance = product.IsCashAdvance,
        IsActive = product.IsActive,
        ParentProductId = product.ParentProductId,
        IsGroupHeader = product.IsGroupHeader,
        IsStockShared = product.IsStockShared,
        HasIndependentPricing = product.HasIndependentPricing,
        ConversionFactor = product.ConversionFactor,
        GroupKey = product.GroupKey
    };

    [Fact]
    public async Task CreateProductFromDtoAsync_WithRoundingRules_PersistsAndReturnsThem()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var dto = CreateDto();
        dto.RoundingRuleUsd = RoundingRuleType.UsdPsychological;
        dto.RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep10;

        var created = await service.CreateProductFromDtoAsync(dto);

        Assert.Equal(RoundingRuleType.UsdPsychological, created.RoundingRuleUsd);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, created.RoundingRuleCashBs);

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(RoundingRuleType.UsdPsychological, persisted.RoundingRuleUsd);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, persisted.RoundingRuleCashBs);

        var reloaded = await service.GetProductDtoByIdAsync(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(RoundingRuleType.UsdPsychological, reloaded!.RoundingRuleUsd);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, reloaded.RoundingRuleCashBs);
    }

    [Fact]
    public async Task CreateProductFromDtoAsync_WithoutRoundingRules_KeepsThemNullInheritingGlobalPolicy()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var created = await service.CreateProductFromDtoAsync(CreateDto());

        Assert.Null(created.RoundingRuleUsd);
        Assert.Null(created.RoundingRuleCashBs);

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Null(persisted.RoundingRuleUsd);
        Assert.Null(persisted.RoundingRuleCashBs);
    }

    [Fact]
    public async Task UpdateProductFromDtoAsync_WithRoundingRules_PersistsThem()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var created = await service.CreateProductFromDtoAsync(CreateDto());

        var update = ToUpdateDto(created);
        update.RoundingRuleUsd = RoundingRuleType.UsdPsychological;
        update.RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep5;

        await service.UpdateProductFromDtoAsync(created.Id, update);

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(RoundingRuleType.UsdPsychological, persisted.RoundingRuleUsd);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, persisted.RoundingRuleCashBs);
    }

    [Fact]
    public async Task UpdateProductFromDtoAsync_WithNullRules_ClearsOverridesBackToGlobalInheritance()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var dto = CreateDto();
        dto.RoundingRuleUsd = RoundingRuleType.UsdPsychological;
        dto.RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyInteger;
        var created = await service.CreateProductFromDtoAsync(dto);

        // El selector "Heredar global" del update viaja como null y debe limpiar el override previo.
        await service.UpdateProductFromDtoAsync(created.Id, ToUpdateDto(created));

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Null(persisted.RoundingRuleUsd);
        Assert.Null(persisted.RoundingRuleCashBs);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task CreateProductFromDtoAsync_UndefinedUsdRule_RejectsWithoutSideEffects(int rawValue)
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var dto = CreateDto();
        dto.RoundingRuleUsd = (RoundingRuleType)rawValue;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProductFromDtoAsync(dto));

        Assert.Equal(nameof(CreateProductDto.RoundingRuleUsd), exception.ParamName);
        Assert.Empty(await context.Products.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(99)]
    public async Task CreateProductFromDtoAsync_UndefinedCashRule_RejectsWithoutSideEffects(int rawValue)
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var dto = CreateDto();
        dto.RoundingRuleCashBs = (RoundingRuleType)rawValue;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProductFromDtoAsync(dto));

        Assert.Equal(nameof(CreateProductDto.RoundingRuleCashBs), exception.ParamName);
        Assert.Empty(await context.Products.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UpdateProductFromDtoAsync_UndefinedRule_RejectsAndLeavesStoredRulesUntouched()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var dto = CreateDto();
        dto.RoundingRuleUsd = RoundingRuleType.UsdPsychological;
        var created = await service.CreateProductFromDtoAsync(dto);

        var update = ToUpdateDto(created);
        update.RoundingRuleCashBs = (RoundingRuleType)99;

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateProductFromDtoAsync(created.Id, update));

        Assert.Equal(nameof(UpdateProductDto.RoundingRuleCashBs), exception.ParamName);

        var persisted = await context.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(RoundingRuleType.UsdPsychological, persisted.RoundingRuleUsd);
        Assert.Null(persisted.RoundingRuleCashBs);
    }
}
