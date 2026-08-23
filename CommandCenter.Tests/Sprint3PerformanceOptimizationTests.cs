using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public class Sprint3PerformanceOptimizationTests
{
    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    [Fact]
    public async Task GetCustomersAsync_RecentOnly_ReturnsDistinctRecentCustomersCorrectly()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<MediatR.IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var salesService = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Seed 4 customers
        context.Customers.AddRange(
            new Customer { Id = 1, Name = "Cliente 1", CedulaOrRif = "V-11111111", IsActive = true },
            new Customer { Id = 2, Name = "Cliente 2", CedulaOrRif = "V-22222222", IsActive = true },
            new Customer { Id = 3, Name = "Cliente 3", CedulaOrRif = "V-33333333", IsActive = true },
            new Customer { Id = 4, Name = "Cliente 4", CedulaOrRif = "V-44444444", IsActive = true }
        );

        // Seed sales with Customer 1 and 2
        context.Sales.AddRange(
            new Sale { Id = 1, CustomerId = 1, Date = DateTime.UtcNow.AddMinutes(-10), TotalUSD = 10, Subtotal = 10, TotalBsS = 500, SubtotalBsS = 500, AppliedRate = 50, Status = SaleStatus.Completed },
            new Sale { Id = 2, CustomerId = 2, Date = DateTime.UtcNow.AddMinutes(-5), TotalUSD = 20, Subtotal = 20, TotalBsS = 1000, SubtotalBsS = 1000, AppliedRate = 50, Status = SaleStatus.Completed }
        );
        await context.SaveChangesAsync();

        var (items, totalCount) = await salesService.GetCustomersAsync(recentOnly: true);
        var list = items.ToList();

        Assert.Equal(3, totalCount);
        Assert.Equal(3, list.Count);
        Assert.Equal(2, list[0].Id); // Most recent first (Customer 2)
        Assert.Equal(1, list[1].Id); // Second most recent (Customer 1)
        Assert.Contains(list, c => c.Id == 4 || c.Id == 3); // Fallback filled to 3
    }

    [Fact]
    public async Task GetSaleAsync_UsesConsolidatedGetSaleEntity_WithCashierAndCustomer()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<MediatR.IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var salesService = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var cashier = new User { Id = 5, Name = "Cajero 1", FullName = "Cajero Principal", Cedula = "V-12345678", IsActive = true };
        var customer = new Customer { Id = 20, Name = "Cliente VIP", CedulaOrRif = "V-98765432", IsActive = true };
        context.Users.Add(cashier);
        context.Customers.Add(customer);

        var sale = new Sale
        {
            Id = 501,
            Status = SaleStatus.Pending,
            CustomerId = 20,
            CashierId = 5,
            Date = DateTime.UtcNow,
            AppliedRate = 50,
            TotalUSD = 10,
            Subtotal = 10,
            TotalBsS = 500,
            SubtotalBsS = 500
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var saleDto = await salesService.GetSaleAsync(501);

        Assert.NotNull(saleDto);
        Assert.Equal(501, saleDto.Id);
        Assert.Equal(5, saleDto.CashierId);
        Assert.Equal("Cajero 1", saleDto.CashierName);
        Assert.NotNull(saleDto.Customer);
        Assert.Equal("Cliente VIP", saleDto.Customer.Name);
    }
}
