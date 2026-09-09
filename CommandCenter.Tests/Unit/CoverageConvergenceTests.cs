using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.26-E4: converge cobertura de dominio en Sales.Module cubriendo los metodos de
// SalesService que quedaban a 0.000 (pickups, cambio de titular, conteos, CRUD de
// cliente, paginacion), la sesion de caja con rollover y el auto-orden de DisplayOrder.
public class CoverageConvergenceTests
{
    private static SalesService CreateSalesService(SalesDbContext context)
    {
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        return new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
    }

    private static Sale PendingPickupSale(int id, int invoice, string customerName, decimal total, DateTime? date = null, int? cashierId = null)
    {
        return new Sale
        {
            Id = id,
            InvoiceNumber = invoice,
            CustomerName = customerName,
            CustomerCedula = "V-12345678",
            TotalUSD = total,
            TotalBsS = total * 40m,
            AppliedRate = 40m,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup,
            Date = date ?? DateTime.UtcNow,
            CashierId = cashierId,
            Items = new List<SaleItem>()
        };
    }

    [Fact]
    public async Task GetPendingPickupsAsync_ReturnsOnlyCompletedPendingPickupSales()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.AddRange(
            PendingPickupSale(1, 1001, "Ana", 50m, DateTime.UtcNow.AddMinutes(-3)),
            PendingPickupSale(2, 1002, "Luis", 80m, DateTime.UtcNow.AddMinutes(-1)),
            new Sale { Id = 3, InvoiceNumber = 1003, CustomerName = "Carla", Status = SaleStatus.Completed, DeliveryStatus = SaleDeliveryStatus.Delivered, Date = DateTime.UtcNow },
            new Sale { Id = 4, InvoiceNumber = 1004, CustomerName = "Dani", Status = SaleStatus.OnHold, DeliveryStatus = SaleDeliveryStatus.PendingPickup, Date = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var result = (await service.GetPendingPickupsAsync()).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.SaleId == 1);
        Assert.Contains(result, p => p.SaleId == 2);
        Assert.Equal(1002, result[0].InvoiceNumber);
        Assert.Equal("Ana", result[1].CustomerName);
    }

    [Fact]
    public async Task GetPendingPickupsAsync_ScopesByCashier()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.AddRange(
            PendingPickupSale(1, 101, "Ana", 10m, cashierId: 1),
            PendingPickupSale(2, 102, "Luis", 20m, cashierId: 2));
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var result = await service.GetPendingPickupsAsync(cashierId: 1);

        Assert.Single(result);
        Assert.Equal(1, result.Single().SaleId);
    }

    [Fact]
    public async Task GetPendingPickupsAsync_AppliesOffsetAndLimitInDescendingDateOrder()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.AddRange(
            PendingPickupSale(1, 101, "Ana", 10m, DateTime.UtcNow.AddHours(-3)),
            PendingPickupSale(2, 102, "Luis", 20m, DateTime.UtcNow.AddHours(-2)),
            PendingPickupSale(3, 103, "Carla", 30m, DateTime.UtcNow.AddHours(-1)));
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var result = (await service.GetPendingPickupsAsync(limit: 1, offset: 1)).ToList();

        Assert.Single(result);
        Assert.Equal(2, result[0].SaleId);
    }

    [Fact]
    public async Task CountPendingPickupsAsync_CountsOnlyCompletedPendingPickupSales()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.AddRange(
            PendingPickupSale(1, 101, "Ana", 10m),
            PendingPickupSale(2, 102, "Luis", 20m),
            new Sale { Id = 3, InvoiceNumber = 103, CustomerName = "Carla", Status = SaleStatus.Completed, DeliveryStatus = SaleDeliveryStatus.Delivered, Date = DateTime.UtcNow },
            new Sale { Id = 4, InvoiceNumber = 104, CustomerName = "Dani", Status = SaleStatus.OnHold, DeliveryStatus = SaleDeliveryStatus.PendingPickup, Date = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        Assert.Equal(2, await service.CountPendingPickupsAsync());
    }

    [Fact]
    public async Task CountPendingPickupsAsync_FiltersByCashier()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.AddRange(
            PendingPickupSale(1, 101, "Ana", 10m, cashierId: 1),
            PendingPickupSale(2, 102, "Luis", 20m, cashierId: 2));
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        Assert.Equal(1, await service.CountPendingPickupsAsync(cashierId: 2));
    }

    [Fact]
    public async Task UpdateSaleCustomerAsync_UpdatesPendingSaleCustomer()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.Add(new Sale { Id = 1, Status = SaleStatus.Pending, Date = DateTime.UtcNow });
        context.Customers.Add(new Customer { Id = 7, CedulaOrRif = "V-87654321", Name = "Maria Perez" });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var result = await service.UpdateSaleCustomerAsync(1, 7);

        Assert.Equal(7, result.CustomerId);
        Assert.Equal("Maria Perez", result.CustomerName);

        var saved = await context.Sales.FindAsync(1);
        Assert.Equal(7, saved!.CustomerId);
        Assert.Equal("Maria Perez", saved.CustomerName);
        Assert.Equal("V-87654321", saved.CustomerCedula);
    }

    [Fact]
    public async Task UpdateSaleCustomerAsync_CompletedSale_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.Add(new Sale { Id = 1, Status = SaleStatus.Completed, Date = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateSaleCustomerAsync(1, 7));

        Assert.Contains("venta finalizada", ex.Message);
    }

    [Fact]
    public async Task UpdateSaleCustomerAsync_OnHoldWithPayments_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo", DisplayOrder = 1 });
        context.Sales.Add(new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            Date = DateTime.UtcNow,
            Payments = new List<SalePayment> { new SalePayment { Id = 1, PaymentMethodId = 1, AmountBsS = 100m } }
        });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateSaleCustomerAsync(1, 7));

        Assert.Contains("cambio de titular", ex.Message);
    }

    [Fact]
    public async Task UpdateSaleCustomerAsync_MissingCustomer_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.Add(new Sale { Id = 1, Status = SaleStatus.Pending, Date = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.UpdateSaleCustomerAsync(1, 999));
    }

    [Fact]
    public async Task CountPendingSalesAsync_CountsOnlyOnHoldSales()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.AddRange(
            new Sale { Id = 1, Status = SaleStatus.OnHold, Date = DateTime.UtcNow },
            new Sale { Id = 2, Status = SaleStatus.OnHold, Date = DateTime.UtcNow },
            new Sale { Id = 3, Status = SaleStatus.Pending, Date = DateTime.UtcNow },
            new Sale { Id = 4, Status = SaleStatus.Completed, Date = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        Assert.Equal(2, await service.CountPendingSalesAsync());
    }

    [Fact]
    public async Task CountPendingSalesAsync_FiltersByCashier()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.AddRange(
            new Sale { Id = 1, Status = SaleStatus.OnHold, CashierId = 1, Date = DateTime.UtcNow },
            new Sale { Id = 2, Status = SaleStatus.OnHold, CashierId = 4, Date = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        Assert.Equal(1, await service.CountPendingSalesAsync(cashierId: 1));
    }

    [Fact]
    public async Task DeleteCustomerAsync_DeletesCustomerWithoutSales()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.Add(new Customer { Id = 3, CedulaOrRif = "V-55555555", Name = "Cliente Libre" });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        await service.DeleteCustomerAsync(3);

        Assert.Equal(0, await context.Customers.CountAsync(c => c.Id == 3));
    }

    [Fact]
    public async Task DeleteCustomerAsync_ConsumidorFinal_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.Add(new Customer { Id = 1, CedulaOrRif = "V-00000000", Name = "Consumidor Final", IsDefault = true });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteCustomerAsync(1));

        Assert.Contains("Consumidor Final", ex.Message);
    }

    [Fact]
    public async Task DeleteCustomerAsync_WithSales_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.Add(new Customer { Id = 4, CedulaOrRif = "V-55555555", Name = "Cliente Con Ventas" });
        context.Sales.Add(new Sale { Id = 1, CustomerId = 4, Status = SaleStatus.Pending, Date = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteCustomerAsync(4));

        Assert.Contains("tiene ventas", ex.Message);
    }

    [Fact]
    public async Task DeleteCustomerAsync_MissingCustomer_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var service = CreateSalesService(context);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteCustomerAsync(999));
    }

    [Fact]
    public async Task GetCustomersAsync_FiltersByNameQuery()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.AddRange(
            new Customer { Id = 1, CedulaOrRif = "V-10000001", Name = "Anabelle Ruiz" },
            new Customer { Id = 2, CedulaOrRif = "V-10000002", Name = "Luis Anaya" },
            new Customer { Id = 3, CedulaOrRif = "V-10000003", Name = "Pedro Gomez" });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var (items, total) = await service.GetCustomersAsync(query: "ana");

        Assert.Equal(2, total);
        Assert.Equal(2, items.Count());
        Assert.Equal("Anabelle Ruiz", items.First().Name);
    }

    [Fact]
    public async Task GetCustomersAsync_FiltersByCedulaQuery()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.AddRange(
            new Customer { Id = 1, CedulaOrRif = "V-10000001", Name = "Anabelle Ruiz" },
            new Customer { Id = 2, CedulaOrRif = "J-20000002", Name = "Luis Anaya" });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var (items, total) = await service.GetCustomersAsync(query: "j-2000");

        Assert.Equal(1, total);
        Assert.Equal("Luis Anaya", items.Single().Name);
    }

    [Fact]
    public async Task GetCustomersAsync_PaginatesOrderedByName()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.AddRange(
            new Customer { Id = 1, CedulaOrRif = "V-10000001", Name = "Delta" },
            new Customer { Id = 2, CedulaOrRif = "V-10000002", Name = "Alpha" },
            new Customer { Id = 3, CedulaOrRif = "V-10000003", Name = "Charlie" },
            new Customer { Id = 4, CedulaOrRif = "V-10000004", Name = "Bravo" },
            new Customer { Id = 5, CedulaOrRif = "V-10000005", Name = "Echo" });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var (items, total) = await service.GetCustomersAsync(page: 2, pageSize: 2);

        Assert.Equal(5, total);
        var names = items.Select(c => c.Name).ToList();
        Assert.Equal(new[] { "Charlie", "Delta" }, names);
    }

    [Fact]
    public async Task GetOrCreateActiveSessionAsync_ReturnsExistingActiveSession()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 10,
            Status = CashDrawerStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            OpeningBalanceLocal = 0m,
            OpeningExchangeRate = 40m
        });
        await context.SaveChangesAsync();

        var service = new CashDrawerService(context);
        var session = await service.GetOrCreateActiveSessionAsync(50m);

        Assert.Equal(10, session.Id);
        Assert.Equal(CashDrawerStatus.Open, session.Status);
    }

    [Fact]
    public async Task GetOrCreateActiveSessionAsync_CarriesOverBalanceFromLastClosedSession()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            Status = CashDrawerStatus.Closed,
            OpenedAt = DateTime.UtcNow.AddDays(-1),
            ClosedAt = DateTime.UtcNow.AddHours(-1),
            OpeningBalanceLocal = 0m,
            ClosingBalanceLocal = 123.45m,
            OpeningExchangeRate = 40m,
            ClosingExchangeRate = 40m
        });
        await context.SaveChangesAsync();

        var service = new CashDrawerService(context);
        var session = await service.GetOrCreateActiveSessionAsync(41m);

        Assert.Equal(CashDrawerStatus.Open, session.Status);
        Assert.Equal(123.45m, session.OpeningBalanceLocal);
        Assert.Equal(41m, session.OpeningExchangeRate);
    }

    [Fact]
    public async Task OpenSessionAsync_NegativeOpeningBalance_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.OpenSessionAsync(-1m, 40m));

        Assert.Contains("no puede ser negativo", ex.Message);
    }

    [Fact]
    public async Task OpenSessionAsync_ZeroExchangeRate_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.OpenSessionAsync(100m, 0m));

        Assert.Contains("mayor a cero", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_WithDefaultDisplayOrder_AssignsNextOrder()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", DisplayOrder = 5, IsActive = true, IsCash = true });
        await context.SaveChangesAsync();

        var service = new PaymentMethodService(context);
        var created = await service.CreateAsync(new PaymentMethod { Name = "Pago Mixto", IsCash = false, IsActive = true, DisplayOrder = 0 });

        Assert.Equal(6, created.DisplayOrder);
        Assert.True(created.Id > 0);
    }
}