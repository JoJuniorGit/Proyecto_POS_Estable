using System;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CommandCenter.Tests.Integration;

[Trait("Category", "RequiresDocker")]
public class ConcurrencyCapacityTests
{
    private const int ProductId = 9000;

    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION") ?? string.Empty;

    private static InventoryDbContext CreateContext(string connString) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(connString).Options);

    private static async Task<string> CreateIsolatedDatabaseAsync(string baseConnString, string dbName)
    {
        var masterCs = new NpgsqlConnectionStringBuilder(baseConnString) { Database = "postgres" }.ConnectionString;
        await using var master = new NpgsqlConnection(masterCs);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{dbName}\"";
        await create.ExecuteNonQueryAsync();
        return dbName;
    }

    private static async Task DropIsolatedDatabaseAsync(string baseConnString, string dbName)
    {
        var masterCs = new NpgsqlConnectionStringBuilder(baseConnString) { Database = "postgres" }.ConnectionString;
        await using var master = new NpgsqlConnection(masterCs);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task SeedProductAsync(string connString, string sku, decimal stock)
    {
        await using var context = CreateContext(connString);
        await context.Database.EnsureCreatedAsync();
        context.Products.Add(new Product
        {
            Id = ProductId,
            SKU = sku,
            Name = "Producto Concurrencia",
            StockQuantity = stock,
            PriceUSD = 10m,
            IsActive = true
        });
        await context.SaveChangesAsync();
    }

    private static Task<bool> DeductOnceAsync(string connString)
    {
        return Task.Run(async () =>
        {
            await using var context = CreateContext(connString);
            var service = new InventoryService(context);
            await service.UpdateStockBatchAsync(new[] { new StockDeductionRequest(ProductId, -1m, "Sale") });
            return true;
        });
    }

    private static async Task<bool> DeductOrFailAsync(string connString)
    {
        try
        {
            await DeductOnceAsync(connString);
            return true;
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Stock insuficiente", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    [Fact]
    public async Task FourConcurrentTerminals_DeductingSameProduct_NoLostUpdatesNorOversell()
    {
        var connStr = ConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION no definida en CI.");
            return;
        }

        var dbName = "pos_conc_" + Guid.NewGuid().ToString("N")[..8];
        var isolated = new NpgsqlConnectionStringBuilder(connStr) { Database = dbName }.ConnectionString;

        try
        {
            await CreateIsolatedDatabaseAsync(connStr, dbName);
            await SeedProductAsync(isolated, "CONC-4", 4m);

            var terminals = Enumerable.Range(0, 4).Select(_ => DeductOnceAsync(isolated));
            var results = await Task.WhenAll(terminals);

            Assert.All(results, r => Assert.True(r));

            await using var context = CreateContext(isolated);
            var final = await context.Products
                .AsNoTracking()
                .Where(p => p.Id == ProductId)
                .Select(p => p.StockQuantity)
                .SingleAsync();
            Assert.Equal(0m, final);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(connStr, dbName);
        }
    }

    [Fact]
    public async Task ConcurrentOversell_DeductingSameProduct_NeverNegativeStock()
    {
        var connStr = ConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION no definida en CI.");
            return;
        }

        var dbName = "pos_conc_" + Guid.NewGuid().ToString("N")[..8];
        var isolated = new NpgsqlConnectionStringBuilder(connStr) { Database = dbName }.ConnectionString;

        try
        {
            await CreateIsolatedDatabaseAsync(connStr, dbName);
            await SeedProductAsync(isolated, "CONC-OVER", 2m);

            var terminals = Enumerable.Range(0, 4).Select(_ => DeductOrFailAsync(isolated));
            var results = await Task.WhenAll(terminals);

            Assert.Equal(2, results.Count(r => r));
            Assert.Equal(2, results.Count(r => !r));

            await using var context = CreateContext(isolated);
            var final = await context.Products
                .AsNoTracking()
                .Where(p => p.Id == ProductId)
                .Select(p => p.StockQuantity)
                .SingleAsync();
            Assert.Equal(0m, final);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(connStr, dbName);
        }
    }
}