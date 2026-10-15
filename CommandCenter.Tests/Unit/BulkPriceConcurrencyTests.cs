using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.181-T1 (S1): el apply masivo usa el camino rastreado (Find/Where + SaveChanges) para conservar
// el token xmin; un producto modificado por otro usuario debe disparar DbUpdateConcurrencyException
// con rollback total (el controller lo mapea a 409). Requiere PostgreSQL (TEST_POSTGRES_CONNECTION);
// sin esa variable el test es no-op, igual que ProductConcurrencyTokenTests.
public class BulkPriceConcurrencyTests
{
    [Fact]
    [Trait("Category", "RequiresDocker")]
    public async Task BulkUpdatePricesAsync_StaleTrackedProduct_ThrowsDbUpdateConcurrencyExceptionAndKeepsWriterValue()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr)) return;

        TestSchemaBootstrap.EnsureSharedSchema(connStr);

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(connStr)
            .Options;

        int productId;
        using (var seedContext = new InventoryDbContext(options))
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
        using var staleContext = new InventoryDbContext(options);
        var staleService = new InventoryService(staleContext);
        await staleContext.Products.FirstAsync(p => p.Id == productId);

        using (var writerContext = new InventoryDbContext(options))
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

        using (var verifyContext = new InventoryDbContext(options))
        {
            var persisted = await verifyContext.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
            Assert.Equal(6m, persisted.PriceRetailUSD); // rollback total: sobrevive el valor del primer writer
        }
    }
}
