using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.185-fix: regresión del desfase del catálogo (p.ej. "7Up 1,5L": la grilla mostraba el Bs.S
/// canónico mientras el POS mostraba el efectivo). Causa raíz: GetProductsPagedAsync no proyectaba
/// RoundingRuleUsd/RoundingRuleCashBs hacia el listado paginado. Cubre además el refresh in-place
/// (UpdateFromDto) que conservaba la regla vieja tras un merge de catálogo.
/// </summary>
public class RoundingCatalogSyncRegressionTests
{
    private static InventoryService CreateService(InventoryDbContext context)
    {
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.CanMutateCatalog).Returns(true);
        user.Setup(u => u.UserId).Returns("rounding-catalog-sync-tests");
        return new InventoryService(context, user.Object);
    }

    private static CreateProductDto CreateDto(string sku) => new()
    {
        Name = $"Producto {sku}",
        SKU = sku,
        CostPriceUSD = 1.00m,
        ProfitMarginRetail = 20m,
        PriceRetailUSD = 1.20m
    };

    [Fact]
    public async Task GetProductsPagedAsync_ProjectsRoundingRulesIntoListItems()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);

        var overrideDto = CreateDto("RND-PAGED-01");
        overrideDto.RoundingRuleUsd = RoundingRuleType.UsdPsychological;
        overrideDto.RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep10;
        var withOverride = await service.CreateProductFromDtoAsync(overrideDto);

        var inherited = await service.CreateProductFromDtoAsync(CreateDto("RND-PAGED-02"));

        var page = await service.GetProductsPagedAsync(filter: null, page: 1, pageSize: 50);

        var overrideRow = page.Items.Single(i => i.Id == withOverride.Id);
        Assert.Equal(RoundingRuleType.UsdPsychological, overrideRow.RoundingRuleUsd);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, overrideRow.RoundingRuleCashBs);

        var inheritedRow = page.Items.Single(i => i.Id == inherited.Id);
        Assert.Null(inheritedRow.RoundingRuleUsd);
        Assert.Null(inheritedRow.RoundingRuleCashBs);
    }

    [Fact]
    public void UpdateFromDto_AdoptsRoundingRulesAndRefreshesVisibleBsS()
    {
        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(r => r.CurrentRate).Returns(36.50m);

        var initial = new ProductDto
        {
            Id = 7,
            Name = "7Up 1,5L",
            SKU = "7U",
            PriceUSD = 12m,
            PriceRetailUSD = 12m,
            PriceBsS = 438m
        };
        var item = new ProductItemViewModel(initial, rateService.Object);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(438m)}", item.DisplayRetailPrice);

        var refreshed = new ProductDto
        {
            Id = 7,
            Name = "7Up 1,5L",
            SKU = "7U",
            PriceUSD = 12m,
            PriceRetailUSD = 12m,
            PriceBsS = 438m,
            RoundingRuleUsd = RoundingRuleType.UsdPsychological,
            RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep10
        };
        item.UpdateFromDto(refreshed);

        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, item.GetDto().RoundingRuleCashBs);
        Assert.Equal(RoundingRuleType.UsdPsychological, item.GetDto().RoundingRuleUsd);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(440m)}", item.DisplayRetailPrice);
    }
}
