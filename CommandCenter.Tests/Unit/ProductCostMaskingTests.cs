using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Entities;
using Core.Extensions;
using Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ProductCostMaskingTests
{
    [Fact]
    public async Task ProductsController_GetAll_AsCashier_MasksCostsAndMarginsOnEveryItem()
    {
        var paged = new PagedResultDto<ProductDto>(
            new List<ProductDto> { CreateProductDto(1), CreateProductDto(2) },
            totalCount: 2);
        var mockService = new Mock<IInventoryService>();
        mockService
            .Setup(s => s.GetProductsPagedAsync(null, 1, 50, null, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(paged);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(false);

        var controller = new ProductsController(mockService.Object, mockUser.Object);
        var result = await controller.GetAllAsync();

        var payload = Assert.IsType<PagedResultDto<ProductDto>>(result.Value);
        var items = payload.Items.ToList();
        Assert.Equal(2, items.Count);
        foreach (var item in items)
        {
            Assert.Equal($"SKU-{item.Id}", item.SKU);
            Assert.Equal(0m, item.CostPriceUSD);
            Assert.Equal(0m, item.Cost);
            Assert.Equal(0m, item.ProfitMarginRetail);
            Assert.Equal(0m, item.ProfitMarginWholesale);
            Assert.Equal(0m, item.ProfitPercentage);
            Assert.Equal(12m, item.PriceRetailUSD);
            Assert.Equal(10m, item.PriceUSD);
            Assert.Equal(11m, item.PriceWholesaleUSD);
            Assert.Equal(480m, item.PriceBsS);
        }
    }

    [Fact]
    public async Task ProductsController_GetAll_AsManager_KeepsCostsAndMarginsOnEveryItem()
    {
        var paged = new PagedResultDto<ProductDto>(
            new List<ProductDto> { CreateProductDto(1), CreateProductDto(2) },
            totalCount: 2);
        var mockService = new Mock<IInventoryService>();
        mockService
            .Setup(s => s.GetProductsPagedAsync(null, 1, 50, null, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(paged);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);

        var controller = new ProductsController(mockService.Object, mockUser.Object);
        var result = await controller.GetAllAsync();

        var payload = Assert.IsType<PagedResultDto<ProductDto>>(result.Value);
        var items = payload.Items.ToList();
        Assert.Equal(2, items.Count);
        foreach (var item in items)
        {
            Assert.Equal($"SKU-{item.Id}", item.SKU);
            Assert.Equal(6m, item.CostPriceUSD);
            Assert.Equal(6m, item.Cost);
            Assert.Equal(50m, item.ProfitMarginRetail);
            Assert.Equal(45m, item.ProfitMarginWholesale);
            Assert.Equal(100m, item.ProfitPercentage);
            Assert.Equal(12m, item.PriceRetailUSD);
            Assert.Equal(10m, item.PriceUSD);
            Assert.Equal(11m, item.PriceWholesaleUSD);
            Assert.Equal(480m, item.PriceBsS);
        }
    }

    [Fact]
    public async Task ProductsController_GetQuickInfo_AsCashier_MasksProfitPercentageAndKeepsTheRestOfTheFields()
    {
        var info = CreateQuickInfoDto(7);
        var mockService = new Mock<IInventoryService>();
        mockService
            .Setup(s => s.GetProductQuickInfoAsync("SKU-7", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(false);

        var controller = new ProductsController(mockService.Object, mockUser.Object);
        var result = await controller.GetQuickInfoAsync("SKU-7");

        var dto = Assert.IsType<ProductQuickInfoDto>(result.Value);
        AssertQuickInfoFieldsSurvive(dto, 7);
        Assert.Equal(0m, dto.ProfitPercentage);
    }

    [Fact]
    public async Task ProductsController_GetQuickInfo_AsManager_KeepsProfitPercentageAndTheRestOfTheFields()
    {
        var info = CreateQuickInfoDto(7);
        var mockService = new Mock<IInventoryService>();
        mockService
            .Setup(s => s.GetProductQuickInfoAsync("SKU-7", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);

        var controller = new ProductsController(mockService.Object, mockUser.Object);
        var result = await controller.GetQuickInfoAsync("SKU-7");

        var dto = Assert.IsType<ProductQuickInfoDto>(result.Value);
        AssertQuickInfoFieldsSurvive(dto, 7);
        Assert.Equal(100m, dto.ProfitPercentage);
    }

    [Fact]
    public async Task ProductsController_GetSuggestions_AsCashier_MasksProfitPercentageAndKeepsTheRestOfTheFieldsOnEveryItem()
    {
        var suggestions = new List<ProductQuickInfoDto> { CreateQuickInfoDto(1), CreateQuickInfoDto(2) };
        var mockService = new Mock<IInventoryService>();
        mockService
            .Setup(s => s.GetSuggestionsAsync("Arroz", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(suggestions);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(false);

        var controller = new ProductsController(mockService.Object, mockUser.Object);
        var result = await controller.GetSuggestionsAsync("Arroz");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<List<ProductQuickInfoDto>>(ok.Value);
        Assert.Equal(2, payload.Count);
        AssertQuickInfoFieldsSurvive(payload[0], 1);
        AssertQuickInfoFieldsSurvive(payload[1], 2);
        Assert.Equal(0m, payload[0].ProfitPercentage);
        Assert.Equal(0m, payload[1].ProfitPercentage);
    }

    [Fact]
    public async Task ProductsController_GetSuggestions_AsManager_KeepsProfitPercentageAndTheRestOfTheFieldsOnEveryItem()
    {
        var suggestions = new List<ProductQuickInfoDto> { CreateQuickInfoDto(1), CreateQuickInfoDto(2) };
        var mockService = new Mock<IInventoryService>();
        mockService
            .Setup(s => s.GetSuggestionsAsync("Arroz", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(suggestions);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);

        var controller = new ProductsController(mockService.Object, mockUser.Object);
        var result = await controller.GetSuggestionsAsync("Arroz");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<List<ProductQuickInfoDto>>(ok.Value);
        Assert.Equal(2, payload.Count);
        AssertQuickInfoFieldsSurvive(payload[0], 1);
        AssertQuickInfoFieldsSurvive(payload[1], 2);
        Assert.Equal(100m, payload[0].ProfitPercentage);
        Assert.Equal(100m, payload[1].ProfitPercentage);
    }

    [Fact]
    public void MaskCosts_ProductWithVariants_ZeroesCostFieldsOnChildrenAndKeepsPrices()
    {
        var parent = CreateProductDto(1);
        var firstVariant = CreateProductDto(2);
        var secondVariant = CreateProductDto(3);
        var nestedVariant = CreateProductDto(4);
        secondVariant.Variants = new List<ProductDto> { nestedVariant };
        parent.Variants = new List<ProductDto> { firstVariant, secondVariant };

        parent.MaskCosts();

        AssertCostFieldsAreMasked(parent);
        AssertCostFieldsAreMasked(firstVariant);
        AssertCostFieldsAreMasked(secondVariant);
        AssertCostFieldsAreMasked(nestedVariant);

        Assert.Equal(12m, parent.PriceRetailUSD);
        Assert.Equal(12m, firstVariant.PriceRetailUSD);
        Assert.Equal(12m, secondVariant.PriceRetailUSD);
        Assert.Equal(12m, nestedVariant.PriceRetailUSD);
        Assert.Equal(480m, parent.PriceBsS);
        Assert.Equal(480m, nestedVariant.PriceBsS);
    }

    private static ProductDto CreateProductDto(int id)
    {
        return new ProductDto
        {
            Id = id,
            Name = $"Producto {id}",
            SKU = $"SKU-{id}",
            PriceUSD = 10m,
            PriceRetailUSD = 12m,
            PriceWholesaleUSD = 11m,
            PriceBsS = 480m,
            CostPriceUSD = 6m,
            Cost = 6m,
            ProfitMarginRetail = 50m,
            ProfitMarginWholesale = 45m,
            ProfitPercentage = 100m,
            StockQuantity = 20m,
            IsActive = true
        };
    }

    private static ProductQuickInfoDto CreateQuickInfoDto(int id)
    {
        return new ProductQuickInfoDto
        {
            Id = id,
            SKU = $"SKU-{id}",
            Name = $"Producto {id}",
            PriceUSD = 10m,
            PriceRetailUSD = 12m,
            PriceWholesaleUSD = 11m,
            PriceBsS = 480m,
            HasWholesale = true,
            IsFractional = true,
            UnitOfMeasure = UnitOfMeasureType.Kg,
            MinWholesaleQuantity = 6m,
            StockQuantity = 20m,
            IsCashAdvance = false,
            IsActive = true,
            ProfitPercentage = 100m,
            ReservedQuantity = 2m,
            ParentProductId = 30,
            ParentIsStockShared = true,
            IsGroupHeader = false,
            IsStockShared = true,
            HasIndependentPricing = true,
            ConversionFactor = 5m,
            VariantCount = 2,
            ConsolidatedStock = 40m
        };
    }

    private static void AssertQuickInfoFieldsSurvive(ProductQuickInfoDto dto, int expectedId)
    {
        Assert.Equal(expectedId, dto.Id);
        Assert.Equal($"SKU-{expectedId}", dto.SKU);
        Assert.Equal($"Producto {expectedId}", dto.Name);
        Assert.Equal(10m, dto.PriceUSD);
        Assert.Equal(12m, dto.PriceRetailUSD);
        Assert.Equal(11m, dto.PriceWholesaleUSD);
        Assert.Equal(480m, dto.PriceBsS);
        Assert.True(dto.HasWholesale);
        Assert.True(dto.IsFractional);
        Assert.Equal(UnitOfMeasureType.Kg, dto.UnitOfMeasure);
        Assert.Equal(6m, dto.MinWholesaleQuantity);
        Assert.Equal(20m, dto.StockQuantity);
        Assert.False(dto.IsCashAdvance);
        Assert.True(dto.IsActive);
        Assert.Equal(2m, dto.ReservedQuantity);
        Assert.Equal(30, dto.ParentProductId);
        Assert.True(dto.ParentIsStockShared);
        Assert.False(dto.IsGroupHeader);
        Assert.True(dto.IsStockShared);
        Assert.True(dto.HasIndependentPricing);
        Assert.Equal(5m, dto.ConversionFactor);
        Assert.Equal(2, dto.VariantCount);
        Assert.Equal(40m, dto.ConsolidatedStock);
        Assert.Equal(18m, dto.AvailableQuantity);
    }

    private static void AssertCostFieldsAreMasked(ProductDto dto)
    {
        Assert.Equal(0m, dto.CostPriceUSD);
        Assert.Equal(0m, dto.Cost);
        Assert.Equal(0m, dto.ProfitMarginRetail);
        Assert.Equal(0m, dto.ProfitMarginWholesale);
        Assert.Equal(0m, dto.ProfitPercentage);
    }
}
