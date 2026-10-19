using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommandCenter.Tests.Integration;
using Core.DTOs;
using Core.Entities;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.181-T1 (S1): el apply masivo usa el camino rastreado (Find/Where + SaveChanges) para conservar
// el token xmin; un producto modificado por otro usuario debe disparar DbUpdateConcurrencyException
// con rollback total (el controller lo mapea a 409). Corre contra PostgreSQL real en base AISLADA
// por test (carril PostgresTestLane); antes usaba la base COMPARTIDA del harness legacy y con
// TEST_POSTGRES_CONNECTION ausente retornaba en silencio. Sin conexion utilizable el lane falla
// EXPLICITO (nunca silent-pass).
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class BulkPriceConcurrencyTests
{
    [Fact]
    public async Task BulkUpdatePricesAsync_StaleTrackedProduct_ThrowsDbUpdateConcurrencyExceptionAndKeepsWriterValue()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(
            applyMigrations: false,
            applyInventoryMigrations: true);

        int productId;
        await using (var seedContext = database.CreateInventoryDbContext())
        {
            var product = new Product
            {
                Name = "Bulk Stale Probe",
                SKU = $"BSTALE-{Guid.NewGuid():N}"[..20],
                PriceRetailUSD = 5m,
                CostPriceUSD = 2m,
                IsActive = true
            };
            seedContext.Products.Add(product);
            await seedContext.SaveChangesAsync();
            productId = product.Id;
        }

        // Contexto "stale": ya tiene el producto rastreado con el xmin previo al cambio ajeno.
        await using var staleContext = database.CreateInventoryDbContext();
        var staleService = new InventoryService(staleContext);
        await staleContext.Products.FirstAsync(p => p.Id == productId);

        await using (var writerContext = database.CreateInventoryDbContext())
        {
            var writerService = new InventoryService(writerContext);
            var writerResult = await writerService.BulkUpdatePricesAsync(new BulkPriceUpdateRequestDto
            {
                ProductIds = new List<int> { productId },
                Operations = new List<BulkPriceOperationDto>
                {
                    new() { Field = BulkPriceField.RetailPrice, Mode = BulkPriceMode.SetFixed, Value = 6m }
                }
            });
            Assert.Equal(1, writerResult.UpdatedCount);
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleService.BulkUpdatePricesAsync(new BulkPriceUpdateRequestDto
        {
            ProductIds = new List<int> { productId },
            Operations = new List<BulkPriceOperationDto>
            {
                new() { Field = BulkPriceField.RetailPrice, Mode = BulkPriceMode.SetFixed, Value = 7m }
            }
        }));

        await using var verifyContext = database.CreateInventoryDbContext();
        var persisted = await verifyContext.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        Assert.Equal(6m, persisted.PriceRetailUSD); // rollback total: sobrevive el valor del primer writer
    }
}
