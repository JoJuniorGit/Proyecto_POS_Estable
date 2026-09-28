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

    public static bool IsPostgreSqlAvailable => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION"));

    public static SalesDbContext? CreatePostgreSqlSalesDbContext()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr)) return null;

        TestSchemaBootstrap.EnsureSharedSchema(connStr);

        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql(connStr)
            .Options;
        return new SalesDbContext(options);
    }

    /// <summary>
    /// 8.9-B4 regression: espejo de la configuracion REAL de Produccion/CI
    /// (Program.cs) con EnableRetryOnFailure(3) -> NpgsqlRetryingExecutionStrategy.
    /// Necesaria para reproducir el fallo "retrying strategy does not support
    /// user-initiated transactions" en queries FromSqlRaw dentro de transaccion manual.
    /// </summary>
    public static SalesDbContext? CreatePostgreSqlSalesDbContextWithRetry()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr)) return null;

        TestSchemaBootstrap.EnsureSharedSchema(connStr);

        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql(connStr, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        return new SalesDbContext(options);
    }

    /// <summary>
    /// 8.143: variante con conexión explícita (BD aislada por test) que conserva
    /// EnableRetryOnFailure(3), espejo de la execution strategy de Producción/CI.
    /// </summary>
    public static SalesDbContext CreatePostgreSqlSalesDbContextWithRetry(string connString)
    {
        TestSchemaBootstrap.EnsureSharedSchema(connString);

        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql(connString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        return new SalesDbContext(options);
    }

    public static (InventoryDbContext context, Microsoft.Data.Sqlite.SqliteConnection connection) CreateSqliteInventoryDbContext()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new InventoryDbContext(options);
        context.Database.EnsureCreated();
        return (context, connection);
    }

    public static (SalesDbContext context, Microsoft.Data.Sqlite.SqliteConnection connection) CreateSqliteSalesDbContext()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new SalesDbContext(options);
        context.Database.EnsureCreated();
        return (context, connection);
    }

    public static SalesDbContext CreateSqliteSalesDbContext(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseSqlite(connection)
            .Options;
        return new SalesDbContext(options);
    }

    public static async Task SeedStandardSalesDataAsync(SalesDbContext context)
    {
        if (context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory" ||
            context.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            await SeedInMemoryAsync(context);
            return;
        }

        await using var conn = new Npgsql.NpgsqlConnection(context.Database.GetConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var lockCmd = new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(42000)", conn, tx);
        await lockCmd.ExecuteScalarAsync();

        var methods = new (int Id, string Name, bool IsCash)[]
        {
            // 8.143: nombres canónicos del fixture, espejo exacto del seed InMemory. El Id=1 debe
            // quedar dolarizado (PaymentMethodCurrencyResolver clasifica por nombre, 8.9-M16).
            // Ojo: el esquema de tests se crea con CreateTablesAsync, que inserta el HasData del
            // modelo ("Cash"/"Card" en Id=1/2); por eso el seed CONVERGE por Id y no asume tabla vacía.
            (1, "Efectivo USD", true),
            (2, "Efectivo Bs.S", true),
            (3, "Punto de Venta", false),
            (4, "Pago Móvil", false),
            (5, "Zelle", false)
        };

        foreach (var (id, name, isCash) in methods)
        {
            // Convergencia del Id canónico: si la fila ya existe (p. ej. HasData), se restauran
            // nombre/tipo y actividad (IsActive/IsDeleted) para que una BD local sucia no degrade
            // el fixture en silencio; se omite si otro registro ya usa el nombre destino.
            await using var convergeCmd = new Npgsql.NpgsqlCommand(
                "UPDATE \"PaymentMethods\" SET \"Name\" = @name, \"IsCash\" = @isCash, \"IsActive\" = true, \"IsDeleted\" = false " +
                "WHERE \"Id\" = @id AND (\"Name\" <> @name OR \"IsCash\" <> @isCash OR \"IsActive\" = false OR \"IsDeleted\" = true) " +
                "AND NOT EXISTS (SELECT 1 FROM \"PaymentMethods\" p2 WHERE p2.\"Name\" = @name AND p2.\"Id\" <> @id)",
                conn, tx);
            convergeCmd.Parameters.AddWithValue("@id", id);
            convergeCmd.Parameters.AddWithValue("@name", name);
            convergeCmd.Parameters.AddWithValue("@isCash", isCash);
            await convergeCmd.ExecuteNonQueryAsync();

            await using var idCheck = new Npgsql.NpgsqlCommand(
                "SELECT COUNT(*) FROM \"PaymentMethods\" WHERE \"Id\" = @id",
                conn, tx);
            idCheck.Parameters.AddWithValue("@id", id);
            var idExists = (long)(await idCheck.ExecuteScalarAsync())! > 0;
            if (idExists) continue;

            await using var nameCheck = new Npgsql.NpgsqlCommand(
                "SELECT COUNT(*) FROM \"PaymentMethods\" WHERE \"Name\" = @name AND \"IsDeleted\" = false",
                conn, tx);
            nameCheck.Parameters.AddWithValue("@name", name);
            var nameExists = (long)(await nameCheck.ExecuteScalarAsync())! > 0;
            if (nameExists) continue;

            await using var insertCmd = new Npgsql.NpgsqlCommand(
                "INSERT INTO \"PaymentMethods\" (\"Id\", \"Name\", \"IsCash\", \"IsActive\", \"IsDeleted\", \"RequiresReference\", \"DisplayOrder\") VALUES (@id, @name, @isCash, true, false, false, @id)",
                conn, tx);
            insertCmd.Parameters.AddWithValue("@id", id);
            insertCmd.Parameters.AddWithValue("@name", name);
            insertCmd.Parameters.AddWithValue("@isCash", isCash);
            await insertCmd.ExecuteNonQueryAsync();
        }

        await using var custCheck = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM \"Customers\" WHERE \"Id\" = 1 OR \"IsDefault\" = true",
            conn, tx);
        var custExists = (long)(await custCheck.ExecuteScalarAsync())! > 0;
        if (!custExists)
        {
            await using var insertCust = new Npgsql.NpgsqlCommand(
                "INSERT INTO \"Customers\" (\"Id\", \"Name\", \"CedulaOrRif\", \"Phone\", \"CreditLimitUSD\", \"IsDefault\", \"IsActive\", \"CreatedAt\") VALUES (1, 'Consumidor Final', 'V-00000000', '', 0, true, true, NOW())",
                conn, tx);
            await insertCust.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private static async Task SeedInMemoryAsync(SalesDbContext context)
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
