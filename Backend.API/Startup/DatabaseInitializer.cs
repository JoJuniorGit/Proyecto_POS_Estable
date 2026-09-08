using System;
using System.Threading.Tasks;
using Core.Logging;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Backend.API.Startup;

/// <summary>
/// Centraliza la inicialización de la base de datos en el arranque: verificación de credenciales,
/// creación idempotente de la BD, aplicación de migraciones EF, convergencia de esquema legacy y
/// sembrado de datos iniciales. Se extrajo del monolito Program.cs (hallazgo B11) para mantener el
/// punto de entrada legible y facilitar su prueba unitaria.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Ejecuta la inicialización completa de la BD con política fail-fast: ante cualquier fallo
    /// crítico registra el error, fija Environment.ExitCode = 1 y devuelve false para que el
    /// arranque se aborte sin servir peticiones.
    /// </summary>
    public static async Task<bool> InitializeAsync(WebApplication app, string? connectionString)
    {
        using var scope = app.Services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var salesDb = scope.ServiceProvider.GetRequiredService<Sales.Module.Data.SalesDbContext>();
        var invDb = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var criticalMsg = "[ERROR CRÍTICO] No se encontró la cadena de conexión ConnectionStrings:DefaultConnection. " +
                "Establezca la variable de entorno ConnectionStrings__DefaultConnection (o el appsettings) antes de arrancar.";
            Console.WriteLine(criticalMsg);
            AppLogger.LogDbError(criticalMsg, "Program.ConnectionString");
            Environment.ExitCode = 1;
            return false;
        }

        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        var dbName = csb.Database;
        if (string.IsNullOrWhiteSpace(dbName))
        {
            var criticalMsg = "[ERROR CRÍTICO] La cadena de conexión no especifica la base de datos (Database).";
            Console.WriteLine(criticalMsg);
            AppLogger.LogDbError(criticalMsg, "Program.DatabaseName");
            Environment.ExitCode = 1;
            return false;
        }

        var maintenanceCs = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres"
        }.ConnectionString;

        // 1) Probar conexión contra la BD de mantenimiento "postgres" para distinguir
        //    credenciales incorrectas de base de datos inexistente.
        try
        {
            AppLogger.LogStart($"Probing PostgreSQL maintenance database (postgres) for {dbName}...");
            using var probe = new NpgsqlConnection(maintenanceCs);
            probe.Open();

            // 2) Crear la base de datos si no existe (idempotente).
            using (var cmd = probe.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @db";
                cmd.Parameters.AddWithValue("db", dbName);
                var exists = cmd.ExecuteScalar() != null;
                if (!exists)
                {
                    AppLogger.LogStart($"[START] Creando base de datos {dbName}...");
                    using var create = probe.CreateCommand();
                    create.CommandText = $"CREATE DATABASE \"{dbName}\"";
                    create.ExecuteNonQuery();
                }
            }
        }
        catch (System.Exception connEx)
        {
            var criticalMsg = "[ERROR CRÍTICO] No se pudo conectar a PostgreSQL. " +
                "Verifique que el servicio de BD esté activo y que la variable de entorno " +
                $"ConnectionStrings__DefaultConnection (o el appsettings) sea correcta. {connEx.Message}";
            Console.WriteLine(criticalMsg);
            AppLogger.LogDbError(connEx, "Program.ProbePostgres");
            Environment.ExitCode = 1;
            return false;
        }

        // 3) Aplicar migraciones (crea el esquema y __EFMigrationsHistory). Abortar si fallan.
        try
        {
            AppLogger.LogStart("Running EF Core Database Migrations asynchronously...");
            await invDb.Database.MigrateAsync();
            await salesDb.Database.MigrateAsync();

            // 8.7-B8 (segregación): bloque de CONVERGENCIA LEGACY idempotente (information_schema),
            // solo alcanza a BD instaladas ANTES de que existieran estas migraciones EF. 8.12-B1/B2/B3:
            // las 8 migraciones que estaban HUERFANAS (sin [Migration]) fueron reactivadas con guards
            // idempotentes, por lo que en instalaciones frescas el esquema proviene 100% de las
            // migraciones EF y este bloque es un no-op; en BD legacy actúa de red de seguridad.
            // PendingModelChangesWarning está en modo THROW (ver registros de DbContext) para
            // detectar cualquier divergencia futura del modelo.
            // CONSERVAR: no agregar más ALTER TABLE inline aquí — toda evolución de esquema debe ser una
            // migración EF (dotnet ef migrations add).
            try
            {
                AppLogger.LogStart("Verifying and adjusting database schema and column precision (numeric 18,3)...");

                await salesDb.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'PaymentMethods') THEN
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'PaymentMethods' AND column_name = 'DisplayOrder') THEN
            ALTER TABLE ""PaymentMethods"" ADD COLUMN ""DisplayOrder"" integer NOT NULL DEFAULT 0;
        END IF;
    END IF;
END $$;");

                await salesDb.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'CashTransactions') THEN
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'CashTransactions' AND column_name = 'IsPhysicalCash') THEN
            ALTER TABLE ""CashTransactions"" ADD COLUMN ""IsPhysicalCash"" boolean NOT NULL DEFAULT true;
        END IF;
    END IF;
END $$;");

                await salesDb.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Sales') THEN
        CREATE SEQUENCE IF NOT EXISTS factura_number_seq START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE CACHE 1;
        PERFORM setval('factura_number_seq', GREATEST(COALESCE((SELECT MAX(""InvoiceNumber"") FROM ""Sales""), 0) + 1, 1), false);
    END IF;
END $$;");

                // 1. Sales module: SaleItems.Quantity -> numeric(18,3)
                await salesDb.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'Quantity' AND (data_type <> 'numeric' OR numeric_precision <> 18 OR numeric_scale <> 3)
    ) THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""Quantity"" TYPE numeric(18,3);
        RAISE NOTICE 'Column SaleItems.Quantity altered to numeric(18,3)';
    END IF;
END $$;");

                // 2. Inventory module: Parent table (Products) first
                await invDb.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'StockQuantity' AND (data_type <> 'numeric' OR numeric_precision <> 18 OR numeric_scale <> 3)) THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""StockQuantity"" TYPE numeric(18,3);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'ReservedQuantity' AND (data_type <> 'numeric' OR numeric_precision <> 18 OR numeric_scale <> 3)) THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""ReservedQuantity"" TYPE numeric(18,3);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'LowStockThreshold' AND (data_type <> 'numeric' OR numeric_precision <> 18 OR numeric_scale <> 3)) THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""LowStockThreshold"" TYPE numeric(18,3);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'MinWholesaleQuantity' AND (data_type <> 'numeric' OR numeric_precision <> 18 OR numeric_scale <> 3)) THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""MinWholesaleQuantity"" TYPE numeric(18,3);
    END IF;

    -- Child tables: StockMovements, StockReservations
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'QuantityChange' AND (data_type <> 'numeric' OR numeric_precision <> 18 OR numeric_scale <> 3)) THEN
        ALTER TABLE ""StockMovements"" ALTER COLUMN ""QuantityChange"" TYPE numeric(18,3);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'NewStockLevel' AND (data_type <> 'numeric' OR numeric_precision <> 18 OR numeric_scale <> 3)) THEN
        ALTER TABLE ""StockMovements"" ALTER COLUMN ""NewStockLevel"" TYPE numeric(18,3);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'StockReservations' AND column_name = 'Quantity' AND (data_type <> 'numeric' OR numeric_precision <> 18 OR numeric_scale <> 3)) THEN
        ALTER TABLE ""StockReservations"" ALTER COLUMN ""Quantity"" TYPE numeric(18,3);
    END IF;
END $$;");

                // Phase 4: User Hardening, Token Revocation & Role Migration (Defensive with information_schema checks)
                await salesDb.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Users') THEN
        UPDATE ""Users""
        SET ""Username"" = COALESCE(NULLIF(TRIM(""Username""), ''), NULLIF(TRIM(""Cedula""), ''), 'user_' || ""Id""::text)
        WHERE ""Username"" IS NULL OR TRIM(""Username"") = '';

        IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'Users' AND indexname = 'ix_users_username_lower') THEN
            CREATE UNIQUE INDEX ""ix_users_username_lower"" ON ""Users"" (LOWER(""Username""));
        END IF;

        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Users' AND column_name = 'SecurityStamp') THEN
            ALTER TABLE ""Users"" ADD COLUMN ""SecurityStamp"" character varying(64) NOT NULL DEFAULT '';
        END IF;
        UPDATE ""Users"" SET ""SecurityStamp"" = md5(random()::text || clock_timestamp()::text) WHERE ""SecurityStamp"" = '' OR ""SecurityStamp"" IS NULL;

        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Users' AND column_name = 'AccessFailedCount') THEN
            ALTER TABLE ""Users"" ADD COLUMN ""AccessFailedCount"" integer NOT NULL DEFAULT 0;
        END IF;

        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Users' AND column_name = 'LockoutEndUtc') THEN
            ALTER TABLE ""Users"" ADD COLUMN ""LockoutEndUtc"" timestamp with time zone NULL;
        END IF;

        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Users' AND column_name = 'LastLoginUtc') THEN
            ALTER TABLE ""Users"" ADD COLUMN ""LastLoginUtc"" timestamp with time zone NULL;
        END IF;
    END IF;
END $$;");

                await salesDb.Database.ExecuteSqlRawAsync(@"
                    DO $$
                    DECLARE
                        v_has_migrated boolean;
                        v_anomalous_count integer;
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Users') THEN
                            CREATE TABLE IF NOT EXISTS ""__RoleMigrationApplied"" (""AppliedAt"" timestamp with time zone NOT NULL);
                            SELECT EXISTS (SELECT 1 FROM ""__RoleMigrationApplied"") INTO v_has_migrated;
                            IF NOT v_has_migrated THEN
                                SELECT COUNT(*) INTO v_anomalous_count 
                                FROM ""Users"" 
                                WHERE ""Role"" NOT IN (0, 1, 2, 3, 4);

                                IF v_anomalous_count > 0 THEN
                                    RAISE WARNING 'Se detectaron % usuarios con roles no estándar. Se preservará su valor para auditoría manual.', v_anomalous_count;
                                END IF;

                                UPDATE ""Users"" 
                                SET ""Role"" = CASE 
                                    WHEN ""Role"" = 0 THEN 3  -- Admin previo (0 en C#) -> nuevo Admin (3)
                                    WHEN ""Role"" = 1 AND (LOWER(""Username"") = 'admin' OR LOWER(""Cedula"") = '12345678' OR LOWER(""Username"") = 'v-12345678') THEN 3
                                    WHEN ""Role"" = 1 THEN 1  -- Cashier previo (1) -> nuevo Cashier (1)
                                    WHEN ""Role"" = 2 THEN 4  -- Driver previo (2 en C#) -> nuevo Driver (4)
                                    ELSE ""Role""             -- Preserva valores desconocidos
                                END;

                                INSERT INTO ""__RoleMigrationApplied"" VALUES (NOW());
                            END IF;
                        END IF;
                    END $$;
                ");

                // Upgrade legacy plain-text passwords to PBKDF2 immediately (H-API-23)
                var usersTableExists = (await salesDb.Database.SqlQueryRaw<int>(
                    @"SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Users'"
                ).ToListAsync()).Any();

                if (usersTableExists)
                {
                    var plainUsers = await salesDb.Users
                        .Where(u => !string.IsNullOrEmpty(u.PasswordHash) && !u.PasswordHash.StartsWith("PBKDF2$"))
                        .ToListAsync();
                    foreach (var u in plainUsers)
                    {
                        u.PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(u.PasswordHash);
                        u.MustChangePassword = true;
                    }
                    if (plainUsers.Count > 0)
                    {
                        await salesDb.SaveChangesAsync();
                        AppLogger.LogStart($"[Security] Upgraded {plainUsers.Count} legacy plain-text password(s) to PBKDF2 with MustChangePassword=true.");
                    }
                }

                AppLogger.LogStart("Database schema and column precision verification completed successfully.");
            }
            catch (System.Exception schemaEx)
            {
                AppLogger.LogDbError(schemaEx, "Program.DefensiveSchemaCheck");
            }

            AppLogger.LogStart("EF Core Database Migrations applied successfully.");
        }
        catch (System.Exception ex)
        {
            var criticalMsg = "[ERROR CRÍTICO] Error ejecutando las migraciones de base de datos. Abortando el arranque. " + ex.Message;
            Console.WriteLine(criticalMsg);
            AppLogger.LogDbError(ex, "Database.Migrate");
            Environment.ExitCode = 1;
            return false;
        }

        // 4) Seed del admin: el usuario y la contraseña semilla vienen de la configuración / variables de entorno.
        var seedUsername = !string.IsNullOrWhiteSpace(config["SystemSettings:AdminSeedUsername"])
            ? config["SystemSettings:AdminSeedUsername"]!.Trim()
            : "admin";
        var seedName = !string.IsNullOrWhiteSpace(config["SystemSettings:AdminSeedName"])
            ? config["SystemSettings:AdminSeedName"]!.Trim()
            : "Administrador";
        var seedPassword = config["SystemSettings:AdminSeedPassword"];
        // 8.7-B9: en Producción el arranque es fail-fast si falta la contraseña del Admin seed
        // (nunca hay un default conocido). En Development/otras se continúa sin sembrar el Admin
        // semilla (appsettings.Development.json puede aportarla explícitamente).
        var isProductionSeed = string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production", "Production", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(seedPassword))
        {
            if (isProductionSeed)
            {
                var criticalMsg = "[ERROR CRÍTICO] Falta SystemSettings__AdminSeedPassword. " +
                    "Establezca la variable de entorno del servicio antes de arrancar en Producción.";
                Console.WriteLine(criticalMsg);
                AppLogger.LogDbError(criticalMsg, "Program.SeedPassword");
                Environment.ExitCode = 1;
                return false;
            }

            AppLogger.LogWarn("[SEED] SystemSettings:AdminSeedPassword no configurada en este entorno. Se omite el sembrado del Admin semilla.");
            seedPassword = string.Empty;
        }

        try
        {
            // 8.7-B9: sin contraseña semilla (dev) se conservan los seeds no sensibles
            // (cliente por defecto, producto adelanto) pero NUNCA se hashea una clave vacía.
            bool hasSeedPassword = !string.IsNullOrWhiteSpace(seedPassword);

            // 8.9-B1: en Producción la clave semilla debe cumplir la política de contraseñas
            // (fail-fast, cubre seed de admin nuevo y de admins existentes sin hash). Impide
            // desplegar con claves conocidas por defecto ("Admin123!", "postgres", etc.).
            if (isProductionSeed && hasSeedPassword)
            {
                var (isSeedPolicyValid, seedPolicyError) =
                    new Core.Services.PasswordPolicyService().ValidatePassword(seedPassword, seedUsername);
                if (!isSeedPolicyValid)
                {
                    var policyMsg = "[ERROR CRÍTICO] SystemSettings__AdminSeedPassword no cumple la política de contraseñas: " + seedPolicyError;
                    Console.WriteLine(policyMsg);
                    AppLogger.LogDbError(policyMsg, "Program.SeedPolicy");
                    Environment.ExitCode = 1;
                    return false;
                }
            }

            var seedLower = seedUsername.ToLower();
            var targetAdmin = hasSeedPassword ? salesDb.Users.FirstOrDefault(u => 
                u.Username.ToLower() == seedLower || 
                u.Cedula.ToLower() == seedLower ||
                (u.Role == Core.Entities.UserRole.Admin && (u.Username == "V-12345678" || u.Cedula == "V-12345678"))) : null;

            if (targetAdmin != null)
            {
                if (string.IsNullOrWhiteSpace(targetAdmin.PasswordHash))
                {
                    targetAdmin.PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(seedPassword);
                    targetAdmin.MustChangePassword = true;
                    AppLogger.LogStart($"[Seed] Set password hash for Admin user: {targetAdmin.Username} (MustChangePassword=true)");
                    salesDb.SaveChanges();
                }
            }
            else if (hasSeedPassword)
            {
                var newAdmin = new Core.Entities.User
                {
                    Cedula = seedUsername,
                    Username = seedUsername,
                    Name = seedName,
                    FullName = seedName,
                    PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(seedPassword),
                    Role = Core.Entities.UserRole.Admin,
                    IsActive = true,
                    MustChangePassword = true, // 8.9-B1: rotación obligatoria en el primer login,
                                               // aunque la clave provenga del instalador
                    SecurityStamp = Guid.NewGuid().ToString("N")
                };
                salesDb.Users.Add(newAdmin);
                salesDb.SaveChanges();
                AppLogger.LogStart($"[Seed] Created customized admin user: {seedUsername} ({seedName})");
            }

            // Ensure ALL Admin users in the system have valid security stamp and password hash if missing (never forcibly reactivate inactive admins)
            if (hasSeedPassword)
            {
            var allAdmins = salesDb.Users.Where(u => u.Role == Core.Entities.UserRole.Admin).ToList();
            bool modifiedAdmins = false;
            foreach (var admin in allAdmins)
            {
                if (string.IsNullOrWhiteSpace(admin.PasswordHash))
                {
                    admin.PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(seedPassword);
                    admin.MustChangePassword = true;
                    modifiedAdmins = true;
                    AppLogger.LogStart($"[Seed] Set seed password hash for Admin user: {admin.Username} ({admin.Cedula}) with MustChangePassword=true");
                }
                if (string.IsNullOrWhiteSpace(admin.SecurityStamp))
                {
                    admin.SecurityStamp = Guid.NewGuid().ToString("N");
                    modifiedAdmins = true;
                }
            }
            if (modifiedAdmins)
            {
                salesDb.SaveChanges();
            }
            }

            // Add default customer if not exists
            if (!salesDb.Customers.Any(c => c.IsDefault))
            {
                var existingGeneral = salesDb.Customers.FirstOrDefault(c => c.CedulaOrRif == "V-00000000");
                if (existingGeneral != null)
                {
                    existingGeneral.IsDefault = true;
                    existingGeneral.Name = "CLIENTE GENERAL / CONSUMIDOR FINAL";
                }
                else
                {
                    salesDb.Customers.Add(new Core.Entities.Customer
                    {
                        CedulaOrRif = "V-00000000",
                        Name = "CLIENTE GENERAL / CONSUMIDOR FINAL",
                        Phone = "",
                        CreditLimitUSD = 0,
                        IsActive = true,
                        IsDefault = true
                    });
                }
                salesDb.SaveChanges();
            AppLogger.LogStart("[Seed] Created default Customer.");
            }

            if (!invDb.Products.Any(p => p.IsCashAdvance))
            {
                invDb.Products.Add(new Core.Entities.Product
                {
                    Name = "Adelanto de Efectivo",
                    SKU = "ADV-001",
                    Description = "Producto de sistema para operaciones de adelanto de efectivo en caja",
                    PriceRetailUSD = 0m,
                    StockQuantity = 999999,
                    IsCashAdvance = true,
                    IsActive = true
                });
                invDb.SaveChanges();
                AppLogger.LogStart("[Seed] Created default Cash Advance System Product.");
            }
        }
        catch (System.Exception ex)
        {
            var criticalMsg = "[ERROR CRÍTICO] Error sembrando los datos iniciales. Abortando el arranque. " + ex.Message;
            Console.WriteLine(criticalMsg);
            AppLogger.LogCrash(ex, "SeedDataInitialization");
            Environment.ExitCode = 1;
            return false;
        }

        return true;
    }
}
