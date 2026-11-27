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

        var methods = new (int Id, string Name, bool IsCash, string Currency)[]
        {
            // 8.143: nombres canónicos del fixture, espejo exacto del seed InMemory. Desde 8.201 la
            // moneda es un dato persistido: el seed debe fijarla (el HasData la deja en Bs.S para
            // "Cash"/"Card" y la convergencia por Id renombra sin reclasificar).
            // Ojo: el esquema de tests se crea con CreateTablesAsync, que inserta el HasData del
            // modelo ("Cash"/"Card" en Id=1/2); por eso el seed CONVERGE por Id y no asume tabla vacía.
            (1, "Efectivo USD", true, "USD"),
            (2, "Efectivo Bs.S", true, "Bs.S"),
            (3, "Punto de Venta", false, "Bs.S"),
            (4, "Pago Móvil", false, "Bs.S"),
            (5, "Zelle", false, "Bs.S")
        };

        foreach (var (id, name, isCash, currency) in methods)
        {
            // Convergencia del Id canónico: si la fila ya existe (p. ej. HasData), se restauran
            // nombre/tipo/moneda y actividad (IsActive/IsDeleted) para que una BD local sucia no
            // degrade el fixture en silencio; se omite si otro registro ya usa el nombre destino.
            await using var convergeCmd = new Npgsql.NpgsqlCommand(
                "UPDATE \"PaymentMethods\" SET \"Name\" = @name, \"IsCash\" = @isCash, \"Currency\" = @currency, \"IsActive\" = true, \"IsDeleted\" = false " +
                "WHERE \"Id\" = @id AND (\"Name\" <> @name OR \"IsCash\" <> @isCash OR \"Currency\" IS DISTINCT FROM @currency OR \"IsActive\" = false OR \"IsDeleted\" = true) " +
                "AND NOT EXISTS (SELECT 1 FROM \"PaymentMethods\" p2 WHERE p2.\"Name\" = @name AND p2.\"Id\" <> @id)",
                conn, tx);
            convergeCmd.Parameters.AddWithValue("@id", id);
            convergeCmd.Parameters.AddWithValue("@name", name);
            convergeCmd.Parameters.AddWithValue("@isCash", isCash);
            convergeCmd.Parameters.AddWithValue("@currency", currency);
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
                "INSERT INTO \"PaymentMethods\" (\"Id\", \"Name\", \"IsCash\", \"Currency\", \"IsActive\", \"IsDeleted\", \"RequiresReference\", \"DisplayOrder\") VALUES (@id, @name, @isCash, @currency, true, false, false, @id)",
                conn, tx);
            insertCmd.Parameters.AddWithValue("@id", id);
            insertCmd.Parameters.AddWithValue("@name", name);
            insertCmd.Parameters.AddWithValue("@isCash", isCash);
            insertCmd.Parameters.AddWithValue("@currency", currency);
            await insertCmd.ExecuteNonQueryAsync();
        }

        await using var custCheck = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM \"Customers\" WHERE \"Id\" = 1 OR \"IsDefault\" = true",
            conn, tx);
        var custExists = (long)(await custCheck.ExecuteScalarAsync())! > 0;
        if (!custExists)
        {
            // 8.187: el perfil crediticio es NOT NULL en el modelo (sin default de DB en esquemas
            // creados por EnsureCreated): el INSERT explícito debe fijar los valores seguros
            // (crédito deshabilitado) o la siembra revienta con 23502 en una base recién creada.
            // Los esquemas legados previos a 8.187 no tienen las columnas: se detecta paridad para
            // no romper bases stale (documentadas como descartables).
            await using var creditColumnsCheck = new Npgsql.NpgsqlCommand(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' " +
                "AND table_name = 'Customers' AND column_name = 'IsCreditEnabled'",
                conn, tx);
            bool hasCreditColumns = (long)(await creditColumnsCheck.ExecuteScalarAsync())! > 0;

            string insertCustSql = hasCreditColumns
                ? "INSERT INTO \"Customers\" (\"Id\", \"Name\", \"CedulaOrRif\", \"Phone\", \"CreditLimitUSD\", \"IsDefault\", \"IsActive\", \"CreatedAt\", " +
                  "\"IsCreditEnabled\", \"PaymentTermDays\", \"CreditGraceDays\", \"CreditStatus\") " +
                  "VALUES (1, 'Consumidor Final', 'V-00000000', '', 0, true, true, NOW(), false, 30, 0, 0)"
                : "INSERT INTO \"Customers\" (\"Id\", \"Name\", \"CedulaOrRif\", \"Phone\", \"CreditLimitUSD\", \"IsDefault\", \"IsActive\", \"CreatedAt\") " +
                  "VALUES (1, 'Consumidor Final', 'V-00000000', '', 0, true, true, NOW())";

            await using var insertCust = new Npgsql.NpgsqlCommand(insertCustSql, conn, tx);
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
                new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true, DisplayOrder = 1, Currency = "USD" },
                new PaymentMethod { Id = 2, Name = "Efectivo Bs.S", IsCash = true, IsActive = true, DisplayOrder = 2, Currency = "Bs.S" },
                new PaymentMethod { Id = 3, Name = "Punto de Venta", IsCash = false, IsActive = true, DisplayOrder = 3, Currency = "Bs.S" },
                new PaymentMethod { Id = 4, Name = "Pago Móvil", IsCash = false, IsActive = true, DisplayOrder = 4, Currency = "Bs.S" },
                new PaymentMethod { Id = 5, Name = "Zelle", IsCash = false, IsActive = true, DisplayOrder = 5, Currency = "Bs.S" }
            );
        }

        await context.SaveChangesAsync();
    }
}
