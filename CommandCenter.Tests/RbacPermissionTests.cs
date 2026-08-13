using System;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommandCenter.Tests;

public class MockCurrentUserService : ICurrentUserService
{
    public UserRole? UserRole { get; set; }
    public string? UserId { get; set; }
    public bool CanMutateCatalog => UserRole == null || UserRole != Core.Entities.UserRole.Cashier;
    public bool CanMutateSettings => UserRole == null || UserRole != Core.Entities.UserRole.Cashier;
    public bool CanMutateExchangeRate => UserRole == null || UserRole != Core.Entities.UserRole.Cashier;
}

public class RbacPermissionTests
{
    private InventoryDbContext GetInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task CashierRole_ThrowsUnauthorizedAccessException_OnCreateProduct()
    {
        using var db = GetInMemoryInventoryDbContext();
        var cashierService = new MockCurrentUserService { UserRole = UserRole.Cashier };
        var inventoryService = new InventoryService(db, cashierService);

        var product = new Product { Name = "Soda", SKU = "100001", CostPriceUSD = 1.00m, PriceRetailUSD = 1.50m };

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inventoryService.CreateProductAsync(product));
        Assert.Contains("El usuario actual no tiene permisos para modificar el catálogo", ex.Message);
    }

    [Fact]
    public async Task AdminRole_AllowsCreateProduct()
    {
        using var db = GetInMemoryInventoryDbContext();
        var adminService = new MockCurrentUserService { UserRole = UserRole.Admin };
        var inventoryService = new InventoryService(db, adminService);

        var product = new Product { Name = "Soda", SKU = "100002", CostPriceUSD = 1.00m, PriceRetailUSD = 1.50m };

        var created = await inventoryService.CreateProductAsync(product);
        Assert.NotNull(created);
        Assert.Equal("100002", created.SKU);
    }

    [Fact]
    public async Task CashierRole_ThrowsUnauthorizedAccessException_OnDeleteProduct()
    {
        using var db = GetInMemoryInventoryDbContext();
        var adminService = new MockCurrentUserService { UserRole = UserRole.Admin };
        var inventoryService = new InventoryService(db, adminService);

        var product = new Product { Name = "Juice", SKU = "100003", CostPriceUSD = 1.00m, PriceRetailUSD = 1.50m };
        await inventoryService.CreateProductAsync(product);

        var cashierService = new MockCurrentUserService { UserRole = UserRole.Cashier };
        var cashierInventoryService = new InventoryService(db, cashierService);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cashierInventoryService.DeleteProductAsync(product.Id));
        Assert.Contains("El usuario actual no tiene permisos para modificar el catálogo", ex.Message);
    }
}
