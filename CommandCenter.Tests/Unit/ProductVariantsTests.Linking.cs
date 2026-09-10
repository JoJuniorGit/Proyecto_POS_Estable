using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.DTOs;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public partial class ProductVariantsTests
{
    [Fact]
    public async Task GetCandidateVariantsPagedAsync_ExcludesParent_GroupHeaders_AlreadyLinked_AndCashAdvance()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Id = 100,
            Name = "Camisa Polo Header",
            SKU = "GRP-POLO",
            IsGroupHeader = true,
            IsActive = true
        };
        var childAlreadyLinked = new Product
        {
            Id = 101,
            Name = "Camisa Polo Roja",
            SKU = "POLO-RED",
            ParentProductId = 100,
            IsActive = true
        };
        var otherHeader = new Product
        {
            Id = 102,
            Name = "Pantalones Header",
            SKU = "GRP-PANTS",
            IsGroupHeader = true,
            IsActive = true
        };
        var cashAdvance = new Product
        {
            Id = 103,
            Name = "Avance de Efectivo",
            SKU = "CASH-ADV",
            IsCashAdvance = true,
            IsActive = true
        };
        var inactiveProduct = new Product
        {
            Id = 104,
            Name = "Camisa Polo Inactiva",
            SKU = "POLO-INACT",
            IsActive = false
        };
        var deletedProduct = new Product
        {
            Id = 105,
            Name = "Camisa Polo Eliminada",
            SKU = "POLO-DEL",
            IsDeleted = true,
            IsActive = true
        };
        var validCandidate1 = new Product
        {
            Id = 106,
            Name = "Camisa Polo Azul",
            SKU = "POLO-BLUE",
            IsActive = true,
            PriceRetailUSD = 15m,
            StockQuantity = 10m
        };
        var validCandidate2 = new Product
        {
            Id = 107,
            Name = "Camisa Polo Verde",
            SKU = "POLO-GREEN",
            IsActive = true,
            PriceRetailUSD = 15m,
            StockQuantity = 8m
        };

        db.Products.AddRange(parent, childAlreadyLinked, otherHeader, cashAdvance, inactiveProduct, deletedProduct, validCandidate1, validCandidate2);
        await db.SaveChangesAsync();

        var result = await service.GetCandidateVariantsPagedAsync(100, "POLO", 1, 10);

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Count());
        Assert.Contains(result.Items, p => p.Id == 106);
        Assert.Contains(result.Items, p => p.Id == 107);
        Assert.DoesNotContain(result.Items, p => p.Id == 100); // Excludes parent
        Assert.DoesNotContain(result.Items, p => p.Id == 101); // Excludes already linked
        Assert.DoesNotContain(result.Items, p => p.Id == 102); // Excludes other group headers
        Assert.DoesNotContain(result.Items, p => p.Id == 103); // Excludes cash advance
        Assert.DoesNotContain(result.Items, p => p.Id == 104); // Excludes inactive
        Assert.DoesNotContain(result.Items, p => p.Id == 105); // Excludes deleted
    }

    [Fact]
    public async Task LinkVariantsBatchAsync_LinksMultipleVariants_Atomically_AndSyncsParentPricingWhenNotIndependent()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Id = 200,
            Name = "Pintura Galon Colores",
            SKU = "GRP-PINT",
            IsGroupHeader = true,
            IsStockShared = true,
            HasIndependentPricing = false,
            PriceRetailUSD = 25m,
            CostPriceUSD = 15m,
            ProfitMarginRetail = 66.67m,
            GroupKey = "GRP-PINTURA",
            IsActive = true
        };
        var prod1 = new Product { Id = 201, Name = "Pintura Blanca", SKU = "PINT-BLA", PriceRetailUSD = 10m, StockQuantity = 5m, IsActive = true };
        var prod2 = new Product { Id = 202, Name = "Pintura Negra", SKU = "PINT-NEG", PriceRetailUSD = 12m, StockQuantity = 8m, IsActive = true };

        db.Products.AddRange(parent, prod1, prod2);
        await db.SaveChangesAsync();

        var linked = await service.LinkVariantsBatchAsync(200, new List<int> { 201, 202 });

        Assert.Equal(2, linked.Count);

        var updated1 = await db.Products.FindAsync(201);
        var updated2 = await db.Products.FindAsync(202);

        Assert.Equal(200, updated1!.ParentProductId);
        Assert.Equal(200, updated2!.ParentProductId);
        Assert.Equal("GRP-PINTURA", updated1.GroupKey);
        // Shared stock resets individual stock to 0 and conversion factor to 1
        Assert.Equal(0m, updated1.StockQuantity);
        Assert.Equal(0m, updated2.StockQuantity);
        Assert.Equal(1.0000m, updated1.ConversionFactor);
        // Pricing synced from parent
        Assert.Equal(25m, updated1.PriceRetailUSD);
        Assert.Equal(15m, updated1.CostPriceUSD);
    }

    [Fact]
    public async Task LinkVariantsBatchAsync_ThrowsUnauthorized_WhenUserCannotMutateCatalog()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var cashierMock = new Mock<ICurrentUserService>();
        cashierMock.Setup(u => u.CanMutateCatalog).Returns(false);
        var service = new InventoryService(db, cashierMock.Object);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.LinkVariantsBatchAsync(100, new List<int> { 101 }));
    }

    [Fact]
    public async Task UnlinkVariantAsync_PreservesLowStockThreshold_WhenGreaterThanZero()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Id = 300,
            Name = "Grupo Jugos",
            SKU = "GRP-JUGOS",
            IsGroupHeader = true,
            IsStockShared = true,
            IsActive = true
        };
        var variant = new Product
        {
            Id = 301,
            Name = "Jugo Naranja",
            SKU = "JUGO-NAR",
            ParentProductId = 300,
            ConversionFactor = 2.5m,
            LowStockThreshold = 15m,
            ReservedQuantity = 0m,
            StockQuantity = 0m,
            IsActive = true
        };

        db.Products.AddRange(parent, variant);
        await db.SaveChangesAsync();

        var unlinked = await service.UnlinkVariantAsync(300, 301);

        Assert.NotNull(unlinked);
        var updated = await db.Products.FindAsync(301);
        Assert.Null(updated!.ParentProductId);
        Assert.Equal(1.0000m, updated.ConversionFactor);
        Assert.Equal(0m, updated.StockQuantity);
        Assert.Equal(15m, updated.LowStockThreshold); // Preserved!
    }

    [Fact]
    public async Task UnlinkVariantAsync_NormalizesLowStockThresholdToDefault_WhenZeroOrNegative()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Id = 400,
            Name = "Grupo Harinas",
            SKU = "GRP-HAR",
            IsGroupHeader = true,
            IsStockShared = true,
            IsActive = true
        };
        var variant = new Product
        {
            Id = 401,
            Name = "Harina Trigo 1Kg",
            SKU = "HAR-TRI",
            ParentProductId = 400,
            ConversionFactor = 1.0m,
            LowStockThreshold = 0m, // zero threshold
            ReservedQuantity = 0m,
            StockQuantity = 0m,
            IsActive = true
        };

        db.Products.AddRange(parent, variant);
        await db.SaveChangesAsync();

        await service.UnlinkVariantAsync(400, 401);

        var updated = await db.Products.FindAsync(401);
        Assert.Null(updated!.ParentProductId);
        Assert.Equal(5.000m, updated.LowStockThreshold); // Normalized to default 5!
    }

    [Fact]
    public async Task UnlinkVariantAsync_ThrowsInvalidOperationException_WhenActiveReservationsExist()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Id = 500,
            Name = "Grupo Aceites",
            SKU = "GRP-OIL",
            IsGroupHeader = true,
            IsActive = true
        };
        var variant = new Product
        {
            Id = 501,
            Name = "Aceite Maíz 1L",
            SKU = "OIL-MAIZ",
            ParentProductId = 500,
            ReservedQuantity = 4m, // Active reservation!
            IsActive = true
        };

        db.Products.AddRange(parent, variant);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UnlinkVariantAsync(500, 501));

        Assert.Contains("reserva de stock activa", ex.Message);
        var notModified = await db.Products.FindAsync(501);
        Assert.Equal(500, notModified!.ParentProductId); // Still linked
    }

    [Fact]
    public async Task VariantManagementViewModel_SearchCandidates_PopulatesCandidateList()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        var mockDialogService = new Mock<Desktop.Client.Services.IDialogService>();

        var parentDto = new ProductDto { Id = 600, Name = "Parent Group", IsGroupHeader = true };
        var candidateDto = new ProductDto { Id = 601, Name = "Candidate Prod", SKU = "CAND-1", PriceRetailUSD = 10m, StockQuantity = 20m };
        var pagedResult = new PagedResultDto<ProductDto> { Items = new List<ProductDto> { candidateDto }, TotalCount = 1, HasMore = false };

        mockProductService.Setup(s => s.GetCandidateVariantsPagedAsync(600, It.IsAny<string?>(), 1, 30, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(pagedResult);
        mockProductService.Setup(s => s.GetVariantsAsync(600)).ReturnsAsync(new List<ProductDto>());

        var vm = new Desktop.Client.ViewModels.VariantManagementViewModel(
            mockProductService.Object,
            mockExchangeRate.Object,
            mockDialogService.Object,
            parentDto);

        await vm.SearchCandidatesAsync();

        Assert.Single(vm.CandidateProducts);
        Assert.Equal(601, vm.CandidateProducts[0].Id);
        Assert.False(vm.HasSelectedCandidates);

        // Select candidate
        vm.CandidateProducts[0].IsSelected = true;
        Assert.True(vm.HasSelectedCandidates);
    }

    [Fact]
    public async Task VariantManagementViewModel_LinkSelectedCandidates_CallsBatchLinkAndRefreshes()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        var mockDialogService = new Mock<Desktop.Client.Services.IDialogService>();

        var parentDto = new ProductDto { Id = 700, Name = "Parent Group", IsGroupHeader = true };
        var candidateDto = new ProductDto { Id = 701, Name = "Candidate Prod", SKU = "CAND-701" };
        var linkedVariant = new ProductDto { Id = 701, Name = "Candidate Prod", SKU = "CAND-701", ParentProductId = 700 };

        mockProductService.Setup(s => s.GetCandidateVariantsPagedAsync(700, It.IsAny<string?>(), 1, 30, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new PagedResultDto<ProductDto> { Items = new List<ProductDto> { candidateDto }, TotalCount = 1, HasMore = false });
        mockProductService.Setup(s => s.GetVariantsAsync(700)).ReturnsAsync(new List<ProductDto>());
        mockProductService.Setup(s => s.LinkVariantsBatchAsync(700, It.Is<List<int>>(ids => ids.Contains(701)), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<ProductDto> { linkedVariant });

        var vm = new Desktop.Client.ViewModels.VariantManagementViewModel(
            mockProductService.Object,
            mockExchangeRate.Object,
            mockDialogService.Object,
            parentDto);

        await vm.SearchCandidatesAsync();
        vm.CandidateProducts[0].IsSelected = true;

        await vm.LinkSelectedCandidatesAsync();

        mockProductService.Verify(s => s.LinkVariantsBatchAsync(700, It.Is<List<int>>(ids => ids.Contains(701)), It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        Assert.Single(vm.Variants);
        Assert.Equal(701, vm.Variants[0].Id);
        Assert.False(vm.IsLinkingPanelOpen);
    }

    [Fact]
    public async Task VariantManagementViewModel_UnlinkVariant_CallsServiceAndRemovesItem()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        var mockDialogService = new Mock<Desktop.Client.Services.IDialogService>();
        mockDialogService.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var parentDto = new ProductDto { Id = 800, Name = "Parent Group", IsGroupHeader = true };
        var variantDto = new ProductDto { Id = 801, Name = "Variant To Unlink", SKU = "V-801", ParentProductId = 800 };

        mockProductService.Setup(s => s.GetVariantsAsync(800)).ReturnsAsync(new List<ProductDto> { variantDto });
        mockProductService.Setup(s => s.UnlinkVariantAsync(800, 801, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(variantDto);

        var vm = new Desktop.Client.ViewModels.VariantManagementViewModel(
            mockProductService.Object,
            mockExchangeRate.Object,
            mockDialogService.Object,
            parentDto);

        await vm.LoadVariantsAsync();
        Assert.Single(vm.Variants);

        await vm.UnlinkVariantAsync(vm.Variants[0]);

        mockProductService.Verify(s => s.UnlinkVariantAsync(800, 801, It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        Assert.Empty(vm.Variants);
    }

}
