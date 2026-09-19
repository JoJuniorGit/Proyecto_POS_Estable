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
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class InventoryServiceUnitTests
{
    private (InventoryService service, InventoryDbContext context, Mock<ICurrentUserService> userMock) CreateService(bool canMutateCatalog = true)
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var userMock = new Mock<ICurrentUserService>();
        userMock.Setup(u => u.CanMutateCatalog).Returns(canMutateCatalog);

        var service = new InventoryService(context, userMock.Object);
        return (service, context, userMock);
    }

    [Fact]
    public async Task CreateProductAsync_WithCostAndMargin_CalculatesCeilFiscalRetailPrice()
    {
        var (service, context, _) = CreateService();

        var product = new CreateProductDto
        {
            SKU = "10001",
            Name = "Producto Ceil",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 20.001m // 1.00 * 1.20001 = 1.20001 -> ceil a 1.21
        };

        var created = await service.CreateProductFromDtoAsync(product);

        Assert.NotNull(created);
        Assert.True(created.Id > 0);
        Assert.Equal(1.21m, created.PriceRetailUSD);
    }

    [Fact]
    public async Task CreateProductAsync_WithManualPrice_PreservesManualPriceOverMargin()
    {
        var (service, context, _) = CreateService();

        var product = new CreateProductDto
        {
            SKU = "10002",
            Name = "Producto Precio Manual",
            CostPriceUSD = 10.00m,
            PriceRetailUSD = 15.00m, // Precio manual directo
            ProfitMarginRetail = 0m
        };

        var created = await service.CreateProductFromDtoAsync(product);

        Assert.Equal(15.00m, created.PriceRetailUSD);
        // Debe auto-calcular el margen: ((15 / 10) - 1) * 100 = 50%
        Assert.Equal(50.00m, created.ProfitMarginRetail);
    }

    [Fact]
    public async Task CreateProductAsync_WithWholesale_ConfiguresWholesalePricesAndMinQuantity()
    {
        var (service, context, _) = CreateService();

        var product = new CreateProductDto
        {
            SKU = "10003",
            Name = "Producto Mayor",
            CostPriceUSD = 10.00m,
            ProfitMarginRetail = 30.00m,
            HasWholesale = true,
            ProfitMarginWholesale = 15.00m,
            MinWholesaleQuantity = 12m
        };

        var created = await service.CreateProductFromDtoAsync(product);

        Assert.True(created.HasWholesale);
        Assert.Equal(12m, created.MinWholesaleQuantity);
        Assert.Equal(13.00m, created.PriceRetailUSD);
        Assert.Equal(11.50m, created.PriceWholesaleUSD);
    }

    [Fact]
    public async Task CreateProductAsync_WhenParentProductMissing_ThrowsKeyNotFoundException()
    {
        var (service, context, _) = CreateService();

        var variant = new CreateProductDto
        {
            SKU = "10004",
            Name = "Variante Sin Padre",
            ParentProductId = 999
        };

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateProductFromDtoAsync(variant));
        Assert.Contains("Producto padre con ID 999 no encontrado", ex.Message);
    }

    [Fact]
    public async Task CreateProductAsync_WithInitialStock_WritesSingleInitialLoadMovementForActingUser()
    {
        var (service, context, userMock) = CreateService();
        userMock.Setup(u => u.UserId).Returns("user-42");

        var request = new CreateProductDto
        {
            SKU = "CARGA-01",
            Name = "Producto con Carga Inicial",
            PriceRetailUSD = 1.00m,
            StockQuantity = 24.5m
        };

        var created = await service.CreateProductFromDtoAsync(request);

        var persisted = await context.Products.FindAsync(created.Id);
        Assert.NotNull(persisted);
        Assert.Equal(24.5m, persisted.StockQuantity);

        var movements = await context.StockMovements.ToListAsync();
        var movement = Assert.Single(movements);
        Assert.Equal(created.Id, movement.ProductId);
        Assert.Equal(24.5m, movement.QuantityChange);
        Assert.Equal(24.5m, movement.NewStockLevel);
        Assert.Equal("Carga inicial", movement.Reason);
        Assert.Null(movement.SaleId);
        Assert.Equal("user-42", movement.UserId);
    }

    [Fact]
    public async Task CreateProductAsync_WithZeroStock_DoesNotWriteStockMovement()
    {
        var (service, context, _) = CreateService();

        var request = new CreateProductDto
        {
            SKU = "CARGA-02",
            Name = "Producto sin Stock Inicial",
            PriceRetailUSD = 2.00m,
            StockQuantity = 0m
        };

        var created = await service.CreateProductFromDtoAsync(request);

        var persisted = await context.Products.FindAsync(created.Id);
        Assert.NotNull(persisted);
        Assert.Equal(0m, persisted.StockQuantity);
        Assert.Empty(await context.StockMovements.ToListAsync());
    }

    [Fact]
    public async Task CreateProductAsync_GroupHeaderWithoutSharedStock_ForcesZeroAndDoesNotWriteStockMovement()
    {
        var (service, context, _) = CreateService();

        var created = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Grupo sin Stock Compartido",
            IsGroupHeader = true,
            IsStockShared = false,
            StockQuantity = 500m
        });

        var persisted = await context.Products.FindAsync(created.Id);
        Assert.NotNull(persisted);
        Assert.Equal(0m, persisted.StockQuantity);
        Assert.Empty(await context.StockMovements.ToListAsync());
    }

    [Fact]
    public async Task CreateSystemProductAsync_WithPositiveStock_WritesInitialLoadMovementForActingUser()
    {
        var (service, context, userMock) = CreateService(canMutateCatalog: false);
        userMock.Setup(u => u.UserId).Returns("system-user");

        var id = await service.CreateSystemProductAsync(new CreateSystemProductRequest
        {
            Name = "Producto de Sistema con Stock",
            SKU = "SYS-CARGA-01",
            PriceRetailUSD = 3.00m,
            StockQuantity = 7m,
            IsCashAdvance = false,
            IsActive = true
        });

        var movements = await context.StockMovements.ToListAsync();
        var movement = Assert.Single(movements);
        Assert.Equal(id, movement.ProductId);
        Assert.Equal(7m, movement.QuantityChange);
        Assert.Equal(7m, movement.NewStockLevel);
        Assert.Equal("Carga inicial", movement.Reason);
        Assert.Equal("system-user", movement.UserId);
    }

    [Fact]
    public async Task CreateSystemProductAsync_WithDescription_PersistsDescription()
    {
        var (service, context, _) = CreateService(canMutateCatalog: false);

        var id = await service.CreateSystemProductAsync(new CreateSystemProductRequest
        {
            Name = "Producto de Sistema Descripto",
            SKU = "SYS-DESC-01",
            Description = "Producto de sistema para operaciones internas",
            PriceRetailUSD = 2.00m,
            StockQuantity = 0m,
            IsCashAdvance = false,
            IsActive = true
        });

        var persisted = await context.Products.FindAsync(id);
        Assert.NotNull(persisted);
        Assert.Equal("Producto de sistema para operaciones internas", persisted!.Description);
    }

    [Fact]
    public async Task CreateSystemProductAsync_WithoutDescription_PersistsEmptyDescription()
    {
        var (service, context, _) = CreateService(canMutateCatalog: false);

        var id = await service.CreateSystemProductAsync(new CreateSystemProductRequest
        {
            Name = "Producto de Sistema sin Descripcion",
            SKU = "SYS-DESC-02",
            PriceRetailUSD = 2.00m,
            StockQuantity = 0m,
            IsCashAdvance = false,
            IsActive = true
        });

        var persisted = await context.Products.FindAsync(id);
        Assert.NotNull(persisted);
        Assert.Equal(string.Empty, persisted!.Description);
    }

    [Fact]
    public async Task UpdateStockAsync_DeductsExactFractionalQuantity_WithoutTruncation()
    {
        var (service, context, _) = CreateService();

        var product = new ProductBuilder()
            .WithId(1)
            .WithSku("QUESO-01")
            .WithName("Queso")
            .WithStock(10.550m)
            .AsFractional()
            .Build();

        context.Products.Add(product);
        await context.SaveChangesAsync();

        await service.UpdateStockAsync(product.Id, -2.325m, "Venta #1");

        var updated = await context.Products.FindAsync(product.Id);
        Assert.NotNull(updated);
        Assert.Equal(8.225m, updated.StockQuantity);

        var movement = await context.StockMovements.FirstOrDefaultAsync(m => m.ProductId == product.Id);
        Assert.NotNull(movement);
        Assert.Equal(-2.325m, movement.QuantityChange);
        Assert.Equal(8.225m, movement.NewStockLevel);
    }

    [Fact]
    public async Task UpdateStockAsync_WhenInsufficientStock_ThrowsInvalidOperationException()
    {
        var (service, context, _) = CreateService();

        var product = new ProductBuilder()
            .WithId(2)
            .WithStock(5.000m)
            .Build();

        context.Products.Add(product);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStockAsync(product.Id, -10.000m, "Venta #2"));
        Assert.Contains("Stock insuficiente", ex.Message);
    }

    [Fact]
    public async Task GetTodayExchangeRateAsync_ReturnsLatestRate_WhenMultipleDatesExist()
    {
        var (service, context, _) = CreateService();

        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var yesterday = today.AddDays(-1);

        context.ExchangeRateHistory.AddRange(
            new ExchangeRateHistory { Date = yesterday, Rate = 45.50m },
            new ExchangeRateHistory { Date = today, Rate = 52.00m }
        );
        await context.SaveChangesAsync();

        decimal rate = await service.GetTodayExchangeRateAsync();

        Assert.Equal(52.00m, rate);
    }

    [Fact]
    public async Task GetCashAdvanceProductAsync_ReturnsConfiguredAdvanceProduct()
    {
        var (service, context, _) = CreateService();

        var advanceProduct = new Product
        {
            Id = 999,
            SKU = "SYS-ADVANCE",
            Name = "Adelanto de Efectivo",
            IsCashAdvance = true,
            IsActive = true,
            IsDeleted = false
        };
        context.Products.Add(advanceProduct);
        await context.SaveChangesAsync();

        var result = await service.GetCashAdvanceProductAsync();

        Assert.NotNull(result);
        Assert.Equal(999, result.Id);
        Assert.True(result.IsCashAdvance);
    }

    [Fact]
    public async Task CreateProductAsync_WithoutPermission_ThrowsUnauthorizedAccessException()
    {
        var (service, _, _) = CreateService(canMutateCatalog: false);

        var product = new CreateProductDto { SKU = "UNAUTH", Name = "No Permiso" };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateProductFromDtoAsync(product));
    }

    [Fact]
    public async Task InventoryService_CreateCashAdvance_EnforcesUnd_AndRejectsGroupHeaderOrParent()
    {
        var (service, context, _) = CreateService(canMutateCatalog: true);

        // 1. Valid Cash Advance creation forces Und, non-fractional, and 0 stock
        var advanceProduct = new CreateProductDto
        {
            SKU = "1001",
            Name = "Retiro Efectivo",
            IsCashAdvance = true,
            IsFractional = true,
            UnitOfMeasure = UnitOfMeasureType.Kg,
            StockQuantity = 500m,
            LowStockThreshold = 10m
        };

        var created = await service.CreateProductFromDtoAsync(advanceProduct);

        Assert.True(created.IsCashAdvance);
        Assert.False(created.IsFractional);
        Assert.Equal(UnitOfMeasureType.Und, created.UnitOfMeasure);
        Assert.Equal(0m, created.StockQuantity);
        Assert.Equal(0m, created.LowStockThreshold);
        Assert.Equal(0m, created.ReservedQuantity);

        // 2. Reject if IsGroupHeader is true
        var groupAdvance = new CreateProductDto
        {
            SKU = "1002",
            Name = "Grupo Invalido",
            IsCashAdvance = true,
            IsGroupHeader = true
        };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProductFromDtoAsync(groupAdvance));

        // 3. Reject if ParentProductId is set
        var parentProduct = new Product
        {
            SKU = "1003",
            Name = "Padre Valido",
            IsGroupHeader = true
        };
        context.Products.Add(parentProduct);
        await context.SaveChangesAsync();

        var variantAdvance = new CreateProductDto
        {
            SKU = "1004",
            Name = "Variante Invalida",
            IsCashAdvance = true,
            ParentProductId = parentProduct.Id
        };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProductFromDtoAsync(variantAdvance));
    }

    [Fact]
    public async Task InventoryService_AdjustAndReserveStock_WithCashAdvanceProduct_RejectsOrBypassesStockChanges()
    {
        var (service, context, _) = CreateService(canMutateCatalog: true);

        var advanceProduct = new Product
        {
            SKU = "2001",
            Name = "Servicio Retiro",
            IsCashAdvance = true,
            StockQuantity = 0m
        };
        context.Products.Add(advanceProduct);
        await context.SaveChangesAsync();

        // 1. AdjustStock throws InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateStockAsync(advanceProduct.Id, 10m, "Ajuste manual indebido"));

        // 2. ReserveStockAsync returns 0 (bypassed) without exception
        var reservationId = await service.ReserveStockAsync(advanceProduct.Id, 1m, TimeSpan.FromMinutes(10));
        Assert.Equal(0, reservationId);

        // Verify stock remains 0
        var reloaded = await context.Products.FindAsync(advanceProduct.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(0m, reloaded.StockQuantity);
        Assert.Equal(0m, reloaded.ReservedQuantity);
    }

    [Fact]
    public async Task GetProductQuickInfoAsync_WithDeletedProduct_ReturnsNull()
    {
        var (service, context, _) = CreateService();

        var product = new Product
        {
            SKU = "10020",
            Name = "Producto Eliminado QuickCheck",
            PriceUSD = 5.00m,
            IsActive = true,
            IsDeleted = true
        };
        context.Products.Add(product);
        await context.SaveChangesAsync();

        var result = await service.GetProductQuickInfoAsync("10020", useCache: false);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetProductQuickInfoAsync_WithActiveProduct_ReturnsDto()
    {
        var (service, context, _) = CreateService();

        var product = new Product
        {
            SKU = "10021",
            Name = "Producto Activo QuickCheck",
            PriceUSD = 5.00m,
            IsActive = true
        };
        context.Products.Add(product);
        await context.SaveChangesAsync();

        var result = await service.GetProductQuickInfoAsync("10021", useCache: false);

        Assert.NotNull(result);
        Assert.Equal("10021", result.SKU);
        Assert.Equal("Producto Activo QuickCheck", result.Name);
    }
}
