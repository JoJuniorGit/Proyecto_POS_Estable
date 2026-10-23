using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PerformancePhase2Tests
{
    [Fact]
    public void VariantManagementViewModel_ImplementsIDisposable()
    {
        var productMock = new Mock<IProductService>();
        var rateMock = new Mock<IExchangeRateService>();
        var dialogMock = new Mock<IDialogService>();
        var productDto = new ProductDto { Id = 1, Name = "Padre Test", SKU = "PADRE-1" };

        var vm = new VariantManagementViewModel(productMock.Object, rateMock.Object, dialogMock.Object, productDto);
        Assert.IsAssignableFrom<IDisposable>(vm);

        // Calling Dispose() should not throw and safely cleanup CancellationTokenSource
        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);

        // Double dispose should also be safe
        var doubleDisposeEx = Record.Exception(() => vm.Dispose());
        Assert.Null(doubleDisposeEx);
    }

    [Fact]
    public void CustomerPickerViewModel_ImplementsIDisposable()
    {
        var salesMock = new Mock<Desktop.Client.Services.ISalesService>();
        var vm = new CustomerPickerViewModel(salesMock.Object);
        Assert.IsAssignableFrom<IDisposable>(vm);

        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public async Task UnlinkVariantAsync_ReadsParentWithAsNoTracking()
    {
        var options = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new Inventory.Module.Data.InventoryDbContext(options);
        var parent = new Product
        {
            Id = 10,
            Name = "Camisa Padre",
            SKU = "CAM-PADRE",
            IsGroupHeader = true,
            IsStockShared = false,
            StockQuantity = 10m
        };
        var child = new Product
        {
            Id = 11,
            Name = "Camisa Azul M",
            SKU = "CAM-AZUL-M",
            ParentProductId = 10,
            StockQuantity = 5m
        };

        db.Products.AddRange(parent, child);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();

        var currentUser = new Mock<Core.Interfaces.ICurrentUserService>();
        currentUser.Setup(u => u.CanMutateCatalog).Returns(true);
        var service = new InventoryService(db, currentUser.Object);

        var unlinked = await service.UnlinkVariantAsync(10, 11);

        Assert.NotNull(unlinked);
        Assert.Null(unlinked.ParentProductId);

        // Parent should NOT be tracked in ChangeTracker because it was read AsNoTracking
        var parentEntry = db.ChangeTracker.Entries<Product>().FirstOrDefault(e => e.Entity.Id == 10);
        Assert.Null(parentEntry);
    }
}
