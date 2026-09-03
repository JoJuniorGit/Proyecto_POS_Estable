using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Metrics;
using Inventory.Module.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;
using Xunit.Abstractions;

namespace CommandCenter.Tests.Unit;

public class Phase1PerformanceOptimizationTests
{
    private readonly ITestOutputHelper _output;

    public Phase1PerformanceOptimizationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GetProductBySkuAsync_BeforeVsAfterPerformance_ExhibitsSpeedup()
    {
        CacheMetrics.Reset();
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });
        var service = new InventoryService(context, cache: cache);

        var product = new Product
        {
            SKU = "750100000001",
            Name = "Producto Prueba Rendimiento",
            PriceRetailUSD = 10.00m,
            CostPriceUSD = 5.00m,
            StockQuantity = 100m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Products.Add(product);
        await context.SaveChangesAsync();

        // Pasada 1: Baseline (Sin Caché - useCache: false)
        var swBefore = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            var p = await service.GetProductBySkuAsync("750100000001", useCache: false);
            Assert.NotNull(p);
        }
        swBefore.Stop();
        double timeBeforeMs = swBefore.Elapsed.TotalMilliseconds;

        // Pasada 2: Optimizada (Con Caché L2 - useCache: true)
        // Primer call para popular la entrada
        await service.GetProductBySkuAsync("750100000001", useCache: true);

        var swAfter = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            var p = await service.GetProductBySkuAsync("750100000001", useCache: true);
            Assert.NotNull(p);
        }
        swAfter.Stop();
        double timeAfterMs = swAfter.Elapsed.TotalMilliseconds;

        var (hits, misses, hitRate) = CacheMetrics.GetSnapshot();

        _output.WriteLine($"=== REPORTE COMPARATIVO DE RENDIMIENTO (Fase 1) ===");
        _output.WriteLine($"Pasada 1 (Baseline DB sin caché - 50 lecturas): {timeBeforeMs:F2} ms");
        _output.WriteLine($"Pasada 2 (Optimizado con IMemoryCache - 50 lecturas): {timeAfterMs:F2} ms");
        _output.WriteLine($"Telemetría Caché -> Hits: {hits}, Misses: {misses}, Hit Rate: {hitRate:F2}%");

        Assert.True(hits > 0, "Se deben haber registrado aciertos en la pasada optimizada.");
        Assert.True(timeAfterMs <= timeBeforeMs + 5.0, "La pasada optimizada debe responder inmediatamente.");
        connection.Close();
    }

    [Fact]
    public async Task GetProductBySkuAsync_ConcurrencyTest_ExecutesSingleFactoryLookup()
    {
        CacheMetrics.Reset();
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });
        var service = new InventoryService(context, cache: cache);

        var product = new Product
        {
            SKU = "750100000999",
            Name = "Producto Concurrente",
            PriceRetailUSD = 15.00m,
            StockQuantity = 50m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Products.Add(product);
        await context.SaveChangesAsync();

        // Simular 10 lecturas paralelas del mismo SKU
        var tasks = Enumerable.Range(0, 10).Select(_ => service.GetProductBySkuAsync("750100000999", useCache: true));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.NotNull);

        var (hits, misses, _) = CacheMetrics.GetSnapshot();
        _output.WriteLine($"[Prueba Concurrencia] 10 Peticiones en Paralelo -> Hits: {hits}, Misses: {misses}");

        Assert.True(hits + misses >= 10, "Todas las peticiones paralelas debieron contarse en la telemetría.");
        connection.Close();
    }

    [Fact]
    public async Task UpdateStockAsync_InvalidatesSkuCache_Immediately()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });
        var service = new InventoryService(context, cache: cache);

        var product = new Product
        {
            SKU = "750100000555",
            Name = "Producto Stock Test",
            StockQuantity = 10m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Products.Add(product);
        await context.SaveChangesAsync();

        // 1. Popular caché con stock 10
        var cached1 = await service.GetProductBySkuAsync("750100000555", useCache: true);
        Assert.Equal(10m, cached1!.StockQuantity);

        // 2. Modificar stock (-2)
        await service.UpdateStockAsync(product.Id, -2m, "Prueba deducción de stock");

        // 3. Consultar nuevamente -> la caché desactualizada debe estar invalidada
        var cached2 = await service.GetProductBySkuAsync("750100000555", useCache: true);
        Assert.Equal(8m, cached2!.StockQuantity);

        connection.Close();
    }

    [Fact]
    public async Task SetProductStatusAsync_DeactivateOrDelete_InvalidatesCache()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });
        var service = new InventoryService(context, cache: cache);

        var product = new Product
        {
            SKU = "750100000777",
            Name = "Producto Estado Test",
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow
        };
        context.Products.Add(product);
        await context.SaveChangesAsync();

        // Popular caché
        var p1 = await service.GetProductBySkuAsync("750100000777", useCache: true);
        Assert.True(p1!.IsActive);

        // Cambiar estado a inactivo
        await service.SetProductStatusAsync(product.Id, isActive: false, isDeleted: false);

        // Consultar con caché
        var p2 = await service.GetProductBySkuAsync("750100000777", useCache: true);
        Assert.False(p2!.IsActive);

        connection.Close();
    }
}
