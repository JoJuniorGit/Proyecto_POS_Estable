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
    public async Task AdjustStockAsync_VariantProduct_UnderIndividualParent_Succeeds()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Zapato Deportivo",
            IsGroupHeader = true,
            IsStockShared = false
        };
        var savedParent = await service.CreateProductAsync(parent);

        var variant = new Product
        {
            Name = "Zapato Talla 42",
            SKU = "7597778889991",
            ParentProductId = savedParent.Id,
            StockQuantity = 10m
        };
        var savedVariant = await service.CreateProductAsync(variant);

        await service.AdjustStockAsync(savedVariant.Id, 5m, "Entrada de lote");

        var updated = await db.Products.FindAsync(savedVariant.Id);
        Assert.NotNull(updated);
        Assert.Equal(15m, updated.StockQuantity);
    }

    [Fact]
    public async Task AdjustStockAsync_StandaloneProduct_Succeeds()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var prod = new Product
        {
            Name = "Arroz 1Kg",
            SKU = "7591234567890",
            StockQuantity = 50m
        };
        var saved = await service.CreateProductAsync(prod);

        await service.AdjustStockAsync(saved.Id, -5m, "Mermas por empaque dañado");

        var updated = await db.Products.FindAsync(saved.Id);
        Assert.NotNull(updated);
        Assert.Equal(45m, updated.StockQuantity);
    }

    [Fact]
    public async Task AdjustStockAsync_CashAdvanceService_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var serviceProd = new Product
        {
            Name = "Adelanto de Efectivo",
            SKU = "999999",
            IsCashAdvance = true
        };
        var saved = await service.CreateProductAsync(serviceProd);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AdjustStockAsync(saved.Id, 100m, "Ajuste a servicio"));

        Assert.Contains("servicio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdjustStockAsync_DeletedProduct_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var prod = new Product
        {
            Name = "Producto Descontinuado",
            SKU = "7593334445556",
            StockQuantity = 20m,
            IsDeleted = true
        };
        var saved = await service.CreateProductAsync(prod);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AdjustStockAsync(saved.Id, 10m, "Ajuste en archivado"));

        Assert.Equal(Core.Constants.InventoryMessages.DeletedProductStockAdjustmentBlocked, ex.Message);
    }

    [Fact]
    public async Task CreateProduct_ConversionFactorOutOfRange_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Huevos Pool",
            IsGroupHeader = true,
            IsStockShared = true
        };
        var savedParent = await service.CreateProductAsync(parent);

        var variant = new Product
        {
            Name = "Variante Excesiva",
            SKU = "7599991112223",
            ParentProductId = savedParent.Id,
            ConversionFactor = 2_000_000m // Mayor a 1,000,000
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateProductAsync(variant));

        Assert.Equal(Core.Constants.InventoryMessages.ConversionFactorOutOfRange, ex.Message);
    }

    [Fact]
    public async Task UpdateStockAsync_HighConcurrency_SharedStockWithConversionFactor_ConsistenceCheck()
    {
        string dbName = Guid.NewGuid().ToString();
        var setupDb = CreateInMemoryInventoryDb(dbName);
        var userMock = CreateAdminUserServiceMock();
        var setupService = new InventoryService(setupDb, userMock.Object);

        // Padre con stock inicial de 1000 unidades base
        var parent = new Product
        {
            Name = "Harina Trigo Saco",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 1000m
        };
        var savedParent = await setupService.CreateProductAsync(parent);

        // Variante de 5 Kg (factor 5)
        var variant5k = new Product
        {
            Name = "Harina Trigo 5Kg",
            SKU = "7595556667771",
            ParentProductId = savedParent.Id,
            ConversionFactor = 5.0m
        };
        var savedVariant = await setupService.CreateProductAsync(variant5k);

        // Simulación de 20 peticiones concurrentes (cada una con su propio DbContext scoped)
        var lockObj = new object();
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() =>
            {
                using var threadDb = CreateInMemoryInventoryDb(dbName);
                var threadService = new InventoryService(threadDb, userMock.Object);
                lock (lockObj)
                {
                    threadService.UpdateStockAsync(savedVariant.Id, -2m, "Venta POS Concurrente").GetAwaiter().GetResult();
                }
            })
        );

        await Task.WhenAll(tasks);

        using var verifyDb = CreateInMemoryInventoryDb(dbName);
        var updatedParent = await verifyDb.Products.FindAsync(savedParent.Id);
        Assert.NotNull(updatedParent);
        Assert.Equal(800m, updatedParent.StockQuantity);
    }

    [Fact]
    public void ProductItemViewModel_CanAdjustStock_MatrixValidation()
    {
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        // 1. Padre con Stock Compartido -> Permite
        var p1 = new ProductDto { Id = 1, Name = "Padre Compartido", IsGroupHeader = true, IsStockShared = true };
        var vm1 = new Desktop.Client.ViewModels.ProductItemViewModel(p1, mockExchangeRate.Object);
        Assert.True(vm1.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipAdjustStockAllowed, vm1.AdjustStockToolTip);

        // 2. Padre con Stock Individual -> Bloqueado
        var p2 = new ProductDto { Id = 2, Name = "Padre Individual", IsGroupHeader = true, IsStockShared = false };
        var vm2 = new Desktop.Client.ViewModels.ProductItemViewModel(p2, mockExchangeRate.Object);
        Assert.False(vm2.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipGroupIndividualBlocked, vm2.AdjustStockToolTip);

        // 3. Variante de Padre Compartido -> Bloqueado
        var p3 = new ProductDto { Id = 3, Name = "Variante Compartida", ParentProductId = 1, ParentIsStockShared = true };
        var vm3 = new Desktop.Client.ViewModels.ProductItemViewModel(p3, mockExchangeRate.Object);
        Assert.False(vm3.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipVariantSharedBlocked, vm3.AdjustStockToolTip);

        // 4. Variante de Padre Individual -> Permite
        var p4 = new ProductDto { Id = 4, Name = "Variante Individual", ParentProductId = 2, ParentIsStockShared = false };
        var vm4 = new Desktop.Client.ViewModels.ProductItemViewModel(p4, mockExchangeRate.Object);
        Assert.True(vm4.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipAdjustStockAllowed, vm4.AdjustStockToolTip);

        // 5. Producto Independiente -> Permite
        var p5 = new ProductDto { Id = 5, Name = "Producto Estandar" };
        var vm5 = new Desktop.Client.ViewModels.ProductItemViewModel(p5, mockExchangeRate.Object);
        Assert.True(vm5.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipAdjustStockAllowed, vm5.AdjustStockToolTip);

        // 6. Servicio / Adelanto de Efectivo -> Bloqueado
        var p6 = new ProductDto { Id = 6, Name = "Adelanto", IsCashAdvance = true };
        var vm6 = new Desktop.Client.ViewModels.ProductItemViewModel(p6, mockExchangeRate.Object);
        Assert.False(vm6.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipCashAdvance, vm6.AdjustStockToolTip);

        // 7. Producto Eliminado -> Bloqueado
        var p7 = new ProductDto { Id = 7, Name = "Archivado", IsDeleted = true };
        var vm7 = new Desktop.Client.ViewModels.ProductItemViewModel(p7, mockExchangeRate.Object);
        Assert.False(vm7.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipDeleted, vm7.AdjustStockToolTip);
    }

    [Fact]
    public async Task ProductsController_AdjustStock_ReturnsExpectedStatusCodes()
    {
        var mockService = new Mock<IInventoryService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);

        var controller = new ProductsController(mockService.Object, mockUser.Object);

        // 1. Éxito -> 204 NoContent
        mockService.Setup(s => s.AdjustStockAsync(1, 10m, "Ajuste OK", null)).Returns(Task.CompletedTask);
        var result204 = await controller.AdjustStock(1, new AdjustStockDto { QuantityChange = 10m, Reason = "Ajuste OK" });
        Assert.IsType<NoContentResult>(result204);

        // 2. Operación Inválida (Bloqueo de inventario) -> 400 BadRequest
        mockService.Setup(s => s.AdjustStockAsync(2, 10m, "Ajuste Bloqueado", null))
            .ThrowsAsync(new InvalidOperationException(Core.Constants.InventoryMessages.GroupIndividualStockAdjustmentBlocked));
        var result400 = await controller.AdjustStock(2, new AdjustStockDto { QuantityChange = 10m, Reason = "Ajuste Bloqueado" });
        Assert.IsType<BadRequestObjectResult>(result400);

        // 3. Usuario sin permisos (Cajero) -> 403 Forbidden
        mockUser.Setup(u => u.CanMutateCatalog).Returns(false);
        var result403 = await controller.AdjustStock(1, new AdjustStockDto { QuantityChange = 10m, Reason = "Ajuste Cajero" });
        var status403 = Assert.IsType<ObjectResult>(result403);
        Assert.Equal(StatusCodes.Status403Forbidden, status403.StatusCode);

        // 4. Producto Inexistente -> 404 NotFound
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);
        mockService.Setup(s => s.AdjustStockAsync(999, 10m, "No Existe", null))
            .ThrowsAsync(new KeyNotFoundException());
        var result404 = await controller.AdjustStock(999, new AdjustStockDto { QuantityChange = 10m, Reason = "No Existe" });
        Assert.IsType<NotFoundResult>(result404);
    }

    [Fact]
    public async Task ProductsController_GetById_AsCashier_CensorsCostAndMargins()
    {
        var product = new Product
        {
            Id = 1,
            Name = "Item de Prueba",
            CostPriceUSD = 50m,
            ProfitMarginRetail = 25m,
            ProfitMarginWholesale = 15m,
            ProfitPercentage = 20m
        };
        var mockService = new Mock<IInventoryService>();
        mockService.Setup(s => s.GetProductByIdAsync(1)).ReturnsAsync(product);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(false);

        var controller = new ProductsController(mockService.Object, mockUser.Object);
        var result = await controller.GetById(1);

        var dto = Assert.IsType<ProductDto>(result.Value);
        Assert.Equal(0m, dto.CostPriceUSD);
        Assert.Equal(0m, dto.Cost);
        Assert.Equal(0m, dto.ProfitMarginRetail);
        Assert.Equal(0m, dto.ProfitMarginWholesale);
        Assert.Equal(0m, dto.ProfitPercentage);
    }

    [Fact]
    public async Task ProductsController_GetById_AsManager_SeesCostAndMargins()
    {
        var product = new Product
        {
            Id = 1,
            Name = "Item de Prueba",
            CostPriceUSD = 50m,
            ProfitMarginRetail = 25m
        };
        var mockService = new Mock<IInventoryService>();
        mockService.Setup(s => s.GetProductByIdAsync(1)).ReturnsAsync(product);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);

        var controller = new ProductsController(mockService.Object, mockUser.Object);
        var result = await controller.GetById(1);

        var dto = Assert.IsType<ProductDto>(result.Value);
        Assert.Equal(50m, dto.CostPriceUSD);
        Assert.Equal(25m, dto.ProfitMarginRetail);
    }

    [Fact]
    public void ProductQuickInfoDto_DisplayPrice_ShowsPreciosIndivWhenIndependentPricingEnabled()
    {
        var dto = new ProductQuickInfoDto
        {
            Id = 1,
            Name = "Camisa Polo",
            IsGroupHeader = true,
            HasIndependentPricing = true,
            PriceBsS = 1000m,
            PriceUSD = 20m
        };

        Assert.Equal("Precios indiv.", dto.DisplayPriceBsS);
        Assert.Equal("Precios indiv.", dto.DisplayPriceUSD);
    }

    [Fact]
    public void ProductQuickInfoDto_DisplayPrice_ShowsNormalPriceWhenPricingIsShared()
    {
        var dto = new ProductQuickInfoDto
        {
            Id = 2,
            Name = "Refresco Sabores",
            IsGroupHeader = true,
            HasIndependentPricing = false,
            PriceBsS = 150.50m,
            PriceUSD = 3.00m
        };

        Assert.Equal("150.50", dto.DisplayPriceBsS);
        Assert.Equal("$3.00", dto.DisplayPriceUSD);
    }



}
