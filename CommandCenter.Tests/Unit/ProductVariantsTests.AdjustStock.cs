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
    public async Task UpdateStockAsync_VariantWithConversionFactor_DeductsMultipliedParentStock()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        // Padre "Caja de Huevos" con 360 unidades de stock base
        var group = new Product
        {
            Name = "Caja de Huevos 360",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 360m,
            PriceRetailUSD = 40.00m,
            CostPriceUSD = 30.00m
        };
        var parent = await service.CreateProductAsync(group);

        // Variante "Cartón" con ConversionFactor = 30 (1 cartón = 30 huevos)
        var cartonVariant = new Product
        {
            Name = "Cartón de Huevos (30 Und)",
            SKU = "759888801",
            ParentProductId = parent.Id,
            ConversionFactor = 30.0m
        };
        var createdVariant = await service.CreateProductAsync(cartonVariant);
        Assert.Equal(30.0m, createdVariant.ConversionFactor);

        // Venta de 2 cartones (-2) -> Debe descontar 2 * 30 = 60 huevos del padre
        await service.UpdateStockAsync(createdVariant.Id, -2m, "Venta de 2 cartones", userId: "1", allowNegativeStock: false);

        var parentInDb = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(parentInDb);
        Assert.Equal(300m, parentInDb.StockQuantity);

        var movements = await db.StockMovements.Where(m => m.ProductId == parent.Id).ToListAsync();
        Assert.Single(movements);
        Assert.Equal(-60m, movements[0].QuantityChange);
        Assert.Equal(300m, movements[0].NewStockLevel);
        Assert.Contains("Cartón de Huevos", movements[0].Reason);
        Assert.Contains("Factor: 30", movements[0].Reason);
    }

    [Fact]
    public async Task UpdateStockAsync_VariantWithConversionFactor_FractionalQuantity_DeductsCorrectly()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Queso Duro Pool (Gramos)",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 5000m // 5000 gramos base
        });

        // Variante "Cuarto de Kilo" -> Factor = 250 gramos
        var variant = await service.CreateProductAsync(new Product
        {
            Name = "Cuarto de Kilo Queso",
            SKU = "759999901",
            ParentProductId = parent.Id,
            ConversionFactor = 250.0m
        });

        // Vender 0.5 unidades de la variante -> 0.5 * 250 = 125 gramos
        await service.UpdateStockAsync(variant.Id, -0.5m, "Venta 0.5 paquete", userId: "1", allowNegativeStock: false);

        var parentInDb = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(parentInDb);
        Assert.Equal(4875m, parentInDb.StockQuantity);
    }

    [Fact]
    public async Task ReserveStockAsync_VariantWithConversionFactor_ReservesMultipliedQuantity_AndSetsSourceProductId()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Caja de Huevos 360",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 360m
        });

        var carton = await service.CreateProductAsync(new Product
        {
            Name = "Cartón de Huevos (30 Und)",
            SKU = "759888802",
            ParentProductId = parent.Id,
            ConversionFactor = 30.0m
        });

        // Reservar 3 cartones -> 3 * 30 = 90 huevos base
        int resId = await service.ReserveStockAsync(carton.Id, 3m, TimeSpan.FromMinutes(15));
        Assert.True(resId > 0);

        var parentInDb = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(parentInDb);
        Assert.Equal(90m, parentInDb.ReservedQuantity);

        var reservation = await db.StockReservations.FindAsync(resId);
        Assert.NotNull(reservation);
        Assert.Equal(parent.Id, reservation.ProductId);
        Assert.Equal(carton.Id, reservation.SourceProductId);
        Assert.Equal(90m, reservation.Quantity);

        // Cancelar reserva -> Desaloja los 90 del padre
        await service.CancelReservationAsync(resId);
        var parentAfterCancel = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(parentAfterCancel);
        Assert.Equal(0m, parentAfterCancel.ReservedQuantity);
    }

    [Fact]
    public async Task CreateProduct_NonSharedParentOrGroupHeader_ForcesConversionFactorToOne()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        // 1. Grupo con factor != 1 -> Debe forzarse a 1.0m
        var group = await service.CreateProductAsync(new Product
        {
            Name = "Grupo Test",
            IsGroupHeader = true,
            IsStockShared = true,
            ConversionFactor = 50.0m
        });
        Assert.Equal(1.0000m, group.ConversionFactor);

        // 2. Variante de padre con IsStockShared = false -> Debe forzarse a 1.0m
        var parentIndividual = await service.CreateProductAsync(new Product
        {
            Name = "Padre No Compartido",
            IsGroupHeader = true,
            IsStockShared = false
        });

        var variantIndep = await service.CreateProductAsync(new Product
        {
            Name = "Variante Individual",
            SKU = "759111222",
            ParentProductId = parentIndividual.Id,
            ConversionFactor = 15.0m
        });
        Assert.Equal(1.0000m, variantIndep.ConversionFactor);
    }

    [Fact]
    public async Task UpdateProductAsync_WhenFactorOmitted_PreservesExistingConversionFactor()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Padre Pool",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 100m
        });

        var variant = await service.CreateProductAsync(new Product
        {
            Name = "Variante 20x",
            SKU = "759333444",
            ParentProductId = parent.Id,
            ConversionFactor = 20.0m
        });

        // Actualizar cambiando el nombre pero enviando ConversionFactor = 0 (omisión en payload parcial)
        variant.Name = "Variante 20x Modificada";
        variant.ConversionFactor = 0m;

        await service.UpdateProductAsync(variant);

        var updated = await service.GetProductByIdAsync(variant.Id);
        Assert.NotNull(updated);
        Assert.Equal("Variante 20x Modificada", updated.Name);
        Assert.Equal(20.0m, updated.ConversionFactor); // Preservado
    }

    [Fact]
    public async Task UpdateStockAsync_ConcurrentVariantDeductionsWithConversionFactor_Relational_DoesNotLoseUpdates()
    {
        var dbName = $"mem_test_{Guid.NewGuid():N}";
        var connString = $"Data Source=file:{dbName}?mode=memory&cache=shared";

        using var masterConn = new Microsoft.Data.Sqlite.SqliteConnection(connString);
        masterConn.Open();

        var masterOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(masterConn)
            .Options;

        int parentId;
        int variantId;

        using (var initDb = new InventoryDbContext(masterOptions))
        {
            initDb.Database.EnsureCreated();

            var parent = new Product
            {
                Name = "Pool Harina Concentrada",
                IsGroupHeader = true,
                IsStockShared = true,
                StockQuantity = 1000m,
                PriceRetailUSD = 1.00m,
                CostPriceUSD = 0.50m
            };
            initDb.Products.Add(parent);
            await initDb.SaveChangesAsync();

            var variant = new Product
            {
                Name = "Bolsa 5Kg",
                SKU = "759555666",
                ParentProductId = parent.Id,
                ConversionFactor = 5.0m,
                IsActive = true
            };
            initDb.Products.Add(variant);
            await initDb.SaveChangesAsync();

            parentId = parent.Id;
            variantId = variant.Id;
        }

        var userMock = CreateAdminUserServiceMock();

        // 10 deducciones concurrentes de 2 unidades cada una (2 bolsas * 5 factor = 10 unidades base por tarea)
        // Total a descontar: 10 * 10 = 100 unidades base.
        int taskCount = 10;
        var tasks = new List<Task>();

        for (int i = 0; i < taskCount; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                using var taskConn = new Microsoft.Data.Sqlite.SqliteConnection(connString);
                taskConn.Open();
                var options = new DbContextOptionsBuilder<InventoryDbContext>()
                    .UseSqlite(taskConn)
                    .Options;
                using var taskContext = new InventoryDbContext(options);
                var taskService = new InventoryService(taskContext, userMock.Object);

                await taskService.UpdateStockAsync(variantId, -2m, "Venta concurrente de bolsa", allowNegativeStock: false);
            }));
        }

        await Task.WhenAll(tasks);

        using (var verifyDb = new InventoryDbContext(masterOptions))
        {
            var parentInDb = await verifyDb.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parentId);
            Assert.NotNull(parentInDb);
            Assert.Equal(900m, parentInDb.StockQuantity); // 1000 - 100 = 900
        }
    }

}
