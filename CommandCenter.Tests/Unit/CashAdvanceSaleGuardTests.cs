using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.149 (SEC-02): los productos de adelanto de efectivo (IsCashAdvance) solo pueden entrar a una
/// venta por el flujo legítimo (CashAdvanceCoordinator → CreateCashAdvanceSaleAsync); las mutaciones
/// de ítems de venta (AddItemAsync / UpdateSaleItemsAsync) deben rechazarlos con el mensaje exacto.
/// </summary>
public class CashAdvanceSaleGuardTests
{
    private const string GuardMessage = "Los productos de adelanto de efectivo no pueden agregarse ni modificarse en una venta; use el flujo de adelanto de efectivo.";
    private const int TestActorId = 42;

    private static SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private static SaleProductInfoDto BuildCashAdvanceProduct() => new()
    {
        Id = 99,
        Name = "Adelanto de Efectivo",
        IsCashAdvance = true,
        IsActive = true,
        PriceUSD = 1m,
        PriceRetailUSD = 1m
    };

    private static SalesService CreateService(SalesDbContext context, IInventoryService inventory)
    {
        return new SalesService(
            context,
            inventory,
            Mock.Of<IMediator>(),
            Mock.Of<ICashDrawerService>(),
            Mock.Of<ISystemSettingsService>());
    }

    [Fact]
    public async Task AddItemAsync_WithCashAdvanceProduct_ThrowsExactGuardMessageAndAddsNothing()
    {
        using var context = GetInMemoryDbContext();
        var product = BuildCashAdvanceProduct();
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetSaleProductByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, inventory.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddItemAsync(1, 99, 1m, 40m));

        Assert.Equal(GuardMessage, ex.Message);

        var persisted = await context.Sales.Include(s => s.Items).AsNoTracking().FirstAsync(s => s.Id == 1);
        Assert.Empty(persisted.Items);
    }

    [Fact]
    public async Task AddItemAsync_WithCashAdvanceProductAndAuthorizedCustomPrice_ThrowsExactGuardMessage()
    {
        using var context = GetInMemoryDbContext();
        var product = BuildCashAdvanceProduct();
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetSaleProductByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, inventory.Object);

        // El guard precede a la lógica de precio: ni siquiera una sobreescritura autorizada lo esquiva.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddItemAsync(1, 99, 1m, 40m, customUnitPriceUsd: 10m, customUnitPriceLocal: 500m, isPriceOverrideAuthorized: true));

        Assert.Equal(GuardMessage, ex.Message);

        var persisted = await context.Sales.Include(s => s.Items).AsNoTracking().FirstAsync(s => s.Id == 1);
        Assert.Empty(persisted.Items);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_WithCashAdvanceProduct_ThrowsExactGuardMessageAndKeepsPersistedItems()
    {
        using var context = GetInMemoryDbContext();
        var product = BuildCashAdvanceProduct();
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SaleProductInfoDto> { product });

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            TotalUSD = 10m,
            AppliedRate = 40m,
            ClaimedByUserId = TestActorId,
            ClaimAction = SaleClaimAction.Editing,
            ClaimedByUserName = "Test Actor",
            ClaimedAtUtc = DateTime.UtcNow
        };
        sale.Items.Add(new SaleItem { ProductId = 50, ProductName = "Original", Quantity = 1m, UnitPrice = 10m, Subtotal = 10m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, inventory.Object);
        var request = new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto>
            {
                // Precio arbitrario + override autorizado: el guard debe dispararse igual (cualquier precio).
                new() { ProductId = 99, Quantity = 1m, UnitPrice = 999m }
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateSaleItemsAsync(1, request, isPriceOverrideAuthorized: true, actingUserId: TestActorId));

        Assert.Equal(GuardMessage, ex.Message);

        var persisted = await context.Sales.Include(s => s.Items).AsNoTracking().FirstAsync(s => s.Id == 1);
        var item = Assert.Single(persisted.Items);
        Assert.Equal(50, item.ProductId);
        Assert.Equal("Original", item.ProductName);
    }
}
