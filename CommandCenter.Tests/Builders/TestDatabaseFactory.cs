using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Entities;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;

namespace CommandCenter.Tests.Builders;

public static class TestDatabaseFactory
{
    public static SalesDbContext CreateSalesDbContext(string? dbName = null)
    {
        var name = dbName ?? Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: name)
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    public static InventoryDbContext CreateInventoryDbContext(string? dbName = null)
    {
        var name = dbName ?? Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: name)
            .Options;
        return new InventoryDbContext(options);
    }

    public static async Task SeedStandardSalesDataAsync(SalesDbContext context)
    {
        if (!await context.Customers.AnyAsync(c => c.IsDefault || c.Id == 1))
        {
            context.Customers.Add(new Customer
            {
                Id = 1,
                Name = "Consumidor Final",
                CedulaOrRif = "V-00000000",
                IsDefault = true,
                IsActive = true
            });
        }

        if (!await context.PaymentMethods.AnyAsync())
        {
            context.PaymentMethods.AddRange(
                new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true, DisplayOrder = 1 },
                new PaymentMethod { Id = 2, Name = "Efectivo Bs.S", IsCash = true, IsActive = true, DisplayOrder = 2 },
                new PaymentMethod { Id = 3, Name = "Punto de Venta", IsCash = false, IsActive = true, DisplayOrder = 3 },
                new PaymentMethod { Id = 4, Name = "Pago Móvil", IsCash = false, IsActive = true, DisplayOrder = 4 },
                new PaymentMethod { Id = 5, Name = "Zelle", IsCash = false, IsActive = true, DisplayOrder = 5 }
            );
        }

        await context.SaveChangesAsync();
    }
}
