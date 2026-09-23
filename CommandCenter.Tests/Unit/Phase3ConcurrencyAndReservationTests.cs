using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Jobs;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase3ConcurrencyAndReservationTests
{
    private SalesDbContext CreateInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SalesDbContext(options);
    }

    private InventoryDbContext CreateInMemoryInventoryDbContext(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task ReservationExpiryJob_ReleasesExpiredUnconfirmedReservations()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<InventoryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddLogging();
        var serviceProvider = services.BuildServiceProvider();

        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var product = new Product
            {
                Id = 100,
                SKU = "EXP-01",
                Name = "Producto Prueba Expiracion",
                StockQuantity = 10m,
                ReservedQuantity = 3m,
                PriceUSD = 5m
            };
            db.Products.Add(product);

            var expiredReservation = new StockReservation
            {
                Id = 1,
                ProductId = 100,
                Quantity = 3m,
                ExpiryDate = DateTime.UtcNow.AddMinutes(-10),
                IsConfirmed = false
            };
            db.StockReservations.Add(expiredReservation);
            await db.SaveChangesAsync();
        }

        var job = new ReservationExpiryJob(serviceProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReservationExpiryJob>.Instance);

        int releasedCount = await job.RunExpiryCycleAsync(CancellationToken.None);

        Assert.Equal(1, releasedCount);

        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var product = await db.Products.FindAsync(100);
            Assert.NotNull(product);
            Assert.Equal(0m, product.ReservedQuantity);

            var remainingReservations = await db.StockReservations.CountAsync();
            Assert.Equal(0, remainingReservations);
        }
    }

    [Fact]
    public async Task CashDrawerService_OpenSession_WhenAlreadyOpen_ThrowsInvalidOperationException()
    {
        using var context = CreateInMemorySalesDbContext();
        var service = new CashDrawerService(context);

        var session1 = await service.OpenSessionAsync(100m, 60m);
        Assert.NotNull(session1);
        Assert.Equal(CashDrawerStatus.Open, session1.Status);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.OpenSessionAsync(50m, 60m);
        });

        Assert.Contains("sesión de caja activa", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CashDrawerService_GetOrCreateActiveSession_ReturnsExistingSessionWithoutDuplication()
    {
        using var context = CreateInMemorySalesDbContext();
        var service = new CashDrawerService(context);

        var session1 = await service.GetOrCreateActiveSessionAsync(60m);
        var session2 = await service.GetOrCreateActiveSessionAsync(60m);

        Assert.Equal(session1.Id, session2.Id);
        var openSessions = await context.CashDrawerSessions.CountAsync(s => s.Status == CashDrawerStatus.Open);
        Assert.Equal(1, openSessions);
    }

    [Fact]
    public async Task Customer_CreateCustomer_WithDuplicateCedulaOrRif_ThrowsInvalidOperationException()
    {
        using var context = CreateInMemorySalesDbContext();
        var service = new SalesService(context, null!, null!, null!, null!);

        var request1 = new CreateCustomerDto
        {
            CedulaOrRif = "V-20111222",
            Name = "Cliente Juan Perez",
            CreditLimitUSD = 100m
        };

        var created1 = await service.CreateCustomerAsync(request1);
        Assert.NotNull(created1);

        var request2 = new CreateCustomerDto
        {
            CedulaOrRif = "v-20111222 ",
            Name = "Cliente Juan Duplicado",
            CreditLimitUSD = 50m
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.CreateCustomerAsync(request2);
        });

        Assert.Contains("Ya existe un cliente registrado", ex.Message);
    }
}
