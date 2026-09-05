using Inventory.Module.Data;
using Inventory.Module.Services;
using Core.Interfaces;
using Core.Logging;
using Microsoft.EntityFrameworkCore;
using DotNetEnv;
using Npgsql;
using Backend.API.Services;
using Backend.API.Hubs;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Security.Cryptography.X509Certificates;

// Setup global unhandled exception logger for crash.log
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    if (e.ExceptionObject is Exception ex)
    {
        AppLogger.LogCrash(ex, "AppDomain.UnhandledException");
    }
};

try
{
    AppLogger.LogStart("Backend API initialization starting...");

    var builder = WebApplication.CreateBuilder(args);

    // HTTPS (contexto seguro requerido por el escáner de cámara desde dispositivos de la red local).
    // Usa el certificado autofirmado pos-https.pfx si existe (ver scripts/create-https-cert.ps1).
    // Si el certificado falta, el servidor continúa sirviendo solo HTTP sin romper el arranque.
    var httpsCert = LoadHttpsCertificate();

    // Limpia la configuración de 'urls' para evitar la advertencia de Kestrel (Overriding address(es)) al definir ListenAnyIP.
    builder.Configuration["urls"] = null;

    int httpPort = int.TryParse(Environment.GetEnvironmentVariable("PORT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORT"), out int p) ? p : 5000;
    int httpsPort = httpPort + 1;

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(httpPort);
        if (httpsCert != null)
        {
            kestrel.ListenAnyIP(httpsPort, listen => listen.UseHttps(httpsCert));
        }
    });

    if (httpsCert != null)
    {
        AppLogger.LogStart("HTTPS habilitado en https://0.0.0.0:5001 (certificado autofirmado)");
    }
    else
    {
        AppLogger.LogStart("[AVISO] Certificado HTTPS no encontrado; el servidor solo escuchará en http://0.0.0.0:5000. Ejecute scripts/create-https-cert.ps1 para habilitar HTTPS.");
    }

    // Enable Windows Service integration (allows sc.exe to manage service natively without Error 1053)
    builder.Host.UseWindowsService();


    // Load .env file searching from BaseDirectory up to root
    try
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            var envCandidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(envCandidate))
            {
                Env.Load(envCandidate);
                break;
            }
            dir = dir.Parent;
        }
    }
    catch { }

    // Prioritize Connection String from appsettings.json / appsettings.Production.json
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    var dbPasswordEnv = Environment.GetEnvironmentVariable("DB_PASSWORD");

    if (!string.IsNullOrEmpty(connectionString))
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(dbPasswordEnv))
        {
            csb.Password = dbPasswordEnv;
        }
        connectionString = csb.ToString();
        AppLogger.LogStart($"DB Connection configured: Host={csb.Host};Port={csb.Port};Database={csb.Database};Username={csb.Username}");
    }

    builder.Services.AddDbContext<InventoryDbContext>(options =>
        options.UseNpgsql(connectionString)
               .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

    builder.Services.AddDbContext<Sales.Module.Data.SalesDbContext>(options =>
        options.UseNpgsql(connectionString, npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
               .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

    builder.Services.AddMemoryCache(options =>
    {
        options.SizeLimit = builder.Configuration.GetValue<int>("MemoryCache:SizeLimit", 10000);
    });
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
    builder.Services.AddScoped<ITokenService, TokenService>();
    builder.Services.AddScoped<ISecurityStampValidator, SecurityStampValidator>();
    builder.Services.AddSingleton<INetworkDiscoveryService, NetworkDiscoveryService>();
    builder.Services.AddScoped<IInventoryService, InventoryService>();
    builder.Services.AddScoped<ISystemSettingsService, SystemSettingsService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.ISalesService, Sales.Module.Services.SalesService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.ICashDrawerService, Sales.Module.Services.CashDrawerService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.IPaymentMethodService, Sales.Module.Services.PaymentMethodService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.IPaymentMethodNotifier, Backend.API.Services.SignalRPaymentMethodNotifier>();
    builder.Services.AddScoped<Sales.Module.Interfaces.IDailyClosureService, Sales.Module.Services.DailyClosureService>();

    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(typeof(Sales.Module.Services.SalesService).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(Inventory.Module.Services.InventoryService).Assembly);
    });

    // BCV Services (Sincronización exclusivamente manual a demanda)
    builder.Services.AddHttpClient<BcvScraperService>();
    builder.Services.AddHostedService<Backend.API.Services.CacheMetricsLoggerService>();
    builder.Services.AddSignalR();

    builder.Services.Configure<Core.Configuration.SystemSettingsOptions>(builder.Configuration.GetSection(Core.Configuration.SystemSettingsOptions.SectionName));

    // JWT Authentication configuration
    var jwtKey = builder.Configuration["JWT_SETTINGS_KEY"] 
              ?? builder.Configuration["JwtSettings:Key"] 
              ?? Environment.GetEnvironmentVariable("JWT_SETTINGS_KEY");

    if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
    {
        if (builder.Environment.IsDevelopment())
        {
            jwtKey = "POS_System_Default_Development_Secret_Key_At_Least_32_Chars!";
        }
        else
        {
            throw new InvalidOperationException("CRITICAL: JWT Secret Key (JWT_SETTINGS_KEY or JwtSettings:Key) must be configured in production and must be at least 32 characters long.");
        }
    }
    else if (!builder.Environment.IsDevelopment() && (jwtKey.Contains("Default_Development_Secret_Key") || jwtKey.Equals("ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795", StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("CRITICAL: Default or historically leaked development JWT secret key cannot be used in production environments.");
    }

    var jwtIssuer = builder.Configuration["JwtSettings:Issuer"] ?? "SolucionesPos";
    var jwtAudience = builder.Configuration["JwtSettings:Audience"] ?? "PosClient";

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token))
                {
                    if (context.Request.Cookies.TryGetValue("pos_jwt", out var cookieToken) && !string.IsNullOrWhiteSpace(cookieToken))
                    {
                        context.Token = cookieToken;
                    }
                }
                return Task.CompletedTask;
            }
        };
    });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("DesktopOnly", policy => policy.RequireClaim("scope", "pos:desktop"));
        options.AddPolicy("WebOnly", policy => policy.RequireClaim("scope", "pos:web"));
        options.AddPolicy("WebOrDesktop", policy => policy.RequireClaim("scope", "pos:web", "pos:desktop"));
    });

    builder.Services.AddControllers(options =>
    {
        options.Filters.Add<Backend.API.Filters.ModelStateValidationFilter>();
    }).AddJsonOptions(x =>
    {
        x.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

    builder.Services.AddOpenApi();
    builder.Services.AddHostedService<Backend.API.Jobs.StockMovementArchiverJob>();
    builder.Services.AddHostedService<Backend.API.Jobs.BcvExchangeRateJob>();
    builder.Services.AddHostedService<Backend.API.Jobs.OutboxProcessorJob>();

    // Rate Limiting (H-15)
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status429TooManyRequests;

        options.AddPolicy("AuthRateLimit", httpContext =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                }));

        options.AddPolicy("GeneralApiRateLimit", httpContext =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 200,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                }));
    });

    // CORS Hardening (H-01): Allow configured origins and local LAN/loopback clients
    var allowedOriginsConfig = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    var allowedOriginsSet = new HashSet<string>(allowedOriginsConfig, StringComparer.OrdinalIgnoreCase);

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.SetIsOriginAllowed(origin =>
            {
                if (string.IsNullOrWhiteSpace(origin)) return false;
                if (allowedOriginsSet.Contains(origin)) return true;

                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                {
                    var host = uri.Host;
                    // Allow localhost / loopback
                    if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("127.0.0.1") || host.Equals("::1"))
                        return true;

                    // Allow Private Intranet Subnets (RFC-1918) for POS LAN network (gated to POS application ports: 5000, 5001, 5173)
                    if (System.Net.IPAddress.TryParse(host, out var ip))
                    {
                        var bytes = ip.GetAddressBytes();
                        if (bytes.Length == 4)
                        {
                            int port = uri.Port;
                            bool isAllowedPort = port == 5000 || port == 5001 || port == 5173;
                            if (isAllowedPort)
                            {
                                if (bytes[0] == 10) return true; // 10.0.0.0/8
                                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true; // 172.16.0.0/12
                                if (bytes[0] == 192 && bytes[1] == 168) return true; // 192.168.0.0/16
                            }
                        }
                    }
                }
                return false;
            })
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
        });
    });

    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
        options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    });

    var app = builder.Build();

    // Global Unhandled Exception & DB Resilience Middleware
    app.UseMiddleware<Backend.API.Middleware.GlobalExceptionHandlerMiddleware>();

    // Security Headers (M-07)
    app.UseMiddleware<Backend.API.Middleware.SecurityHeadersMiddleware>();

    // HTTP Response Compression (Brotli/Gzip)
    app.UseResponseCompression();

    // Serve static files for integrated React Frontend build (wwwroot)
    app.UseDefaultFiles();
    app.UseStaticFiles();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", "Administrador API v1");
        });
    }

    app.UseCors();
    app.UseRateLimiter();

    // Version Compatibility Handshake Middleware
    app.UseMiddleware<Backend.API.Middleware.VersionCheckMiddleware>();

    // Security Audit Middleware for 401 & 403 events
    app.UseMiddleware<Backend.API.Middleware.SecurityAuditMiddleware>();

    app.UseAuthentication();
    app.UseMiddleware<Backend.API.Middleware.SecurityStampValidationMiddleware>();
    app.UseAuthorization();

    app.MapControllers().RequireRateLimiting("GeneralApiRateLimit");
    app.MapHub<ExchangeRateHub>("/hubs/exchange-rate");

    // Fallback SPA routing with strict API 404 segregation (H-API-16)
    app.MapFallback(async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
        {
            context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound,
                Title = "Not Found",
                Detail = $"Ruta de API no encontrada: {context.Request.Path}"
            });
            return;
        }

        var webRoot = app.Environment.WebRootPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var indexPath = System.IO.Path.Combine(webRoot, "index.html");
        if (System.IO.File.Exists(indexPath))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(indexPath);
        }
        else
        {
            context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound;
        }
    });

    // Ensure Database Exists, Migrations and Seed Data (fail-fast: abort startup on any failure)
    using (var scope = app.Services.CreateScope())
    {
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var _salesDb = scope.ServiceProvider.GetRequiredService<Sales.Module.Data.SalesDbContext>();
        var _invDb = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var criticalMsg = "[ERROR CRÍTICO] No se encontró la cadena de conexión ConnectionStrings:DefaultConnection. " +
                "Establezca la variable de entorno ConnectionStrings__DefaultConnection (o el appsettings) antes de arrancar.";
            Console.WriteLine(criticalMsg);
            AppLogger.LogDbError(criticalMsg, "Program.ConnectionString");
            Environment.ExitCode = 1;
            return; // NO seguir arrancando: evita servir peticiones que devuelven 503
        }

        // 1) Probar conexión contra la BD de mantenimiento "postgres" para distinguir
        //    credenciales incorrectas de base de datos inexistente.
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        var dbName = csb.Database;
        if (string.IsNullOrWhiteSpace(dbName))
        {
            var criticalMsg = "[ERROR CRÍTICO] La cadena de conexión no especifica la base de datos (Database).";
            Console.WriteLine(criticalMsg);
            AppLogger.LogDbError(criticalMsg, "Program.DatabaseName");
            Environment.ExitCode = 1;
            return;
        }

        var maintenanceCs = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres"
        }.ConnectionString;

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
            return; // NO seguir arrancando: evita servir peticiones que devuelven 503
        }

        // 3) Aplicar migraciones (crea el esquema y __EFMigrationsHistory). Abortar si fallan.
        try
        {
            AppLogger.LogStart("Running EF Core Database Migrations...");
            _invDb.Database.Migrate();
            _salesDb.Database.Migrate();

            // Defensive schema check & migration: Ensure columns are properly typed in PostgreSQL
            try
            {
                AppLogger.LogStart("Verifying and adjusting database column precision (numeric 18,3)...");

                _salesDb.Database.ExecuteSqlRaw(@"ALTER TABLE ""PaymentMethods"" ADD COLUMN IF NOT EXISTS ""DisplayOrder"" integer NOT NULL DEFAULT 0;");
                _salesDb.Database.ExecuteSqlRaw(@"ALTER TABLE ""CashTransactions"" ADD COLUMN IF NOT EXISTS ""IsPhysicalCash"" boolean NOT NULL DEFAULT true;");
                _salesDb.Database.ExecuteSqlRaw(@"
                    CREATE SEQUENCE IF NOT EXISTS factura_number_seq START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE CACHE 1;
                    SELECT setval('factura_number_seq', GREATEST(COALESCE((SELECT MAX(""InvoiceNumber"") FROM ""Sales""), 0) + 1, 1), false);

                    CREATE TABLE IF NOT EXISTS ""OutboxMessages"" (
                        ""Id"" uuid NOT NULL PRIMARY KEY,
                        ""EventType"" character varying(100) NOT NULL,
                        ""Payload"" jsonb NOT NULL,
                        ""CreatedAtUtc"" timestamp with time zone NOT NULL,
                        ""ProcessedAtUtc"" timestamp with time zone NULL,
                        ""DispatchedAtUtc"" timestamp with time zone NULL,
                        ""Status"" character varying(20) NOT NULL DEFAULT 'Pending',
                        ""RetryCount"" integer NOT NULL DEFAULT 0,
                        ""NextRetryUtc"" timestamp with time zone NOT NULL,
                        ""ErrorMessage"" text NULL
                    );

                    ALTER TABLE ""OutboxMessages"" ADD COLUMN IF NOT EXISTS ""DispatchedAtUtc"" timestamp with time zone NULL;

                    CREATE INDEX IF NOT EXISTS ""IX_OutboxMessages_Status_NextRetryUtc"" 
                        ON ""OutboxMessages"" (""Status"", ""NextRetryUtc"") 
                        WHERE ""Status"" = 'Pending';

                    CREATE INDEX IF NOT EXISTS ""IX_OutboxMessages_CreatedAtUtc"" 
                        ON ""OutboxMessages"" (""CreatedAtUtc"");
                ");
                _salesDb.Database.ExecuteSqlRaw(@"
                    UPDATE ""Users""
                    SET ""Username"" = COALESCE(NULLIF(TRIM(""Username""), ''), NULLIF(TRIM(""Cedula""), ''), 'user_' || ""Id""::text)
                    WHERE ""Username"" IS NULL OR TRIM(""Username"") = '';
                    CREATE UNIQUE INDEX IF NOT EXISTS ""ix_users_username_lower"" ON ""Users"" (LOWER(""Username""));
                ");

                // 1. Sales module: SaleItems.Quantity -> numeric(18,3)
                _salesDb.Database.ExecuteSqlRaw(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'Quantity' AND data_type <> 'numeric'
    ) THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""Quantity"" TYPE numeric(18,3);
        RAISE NOTICE 'Column SaleItems.Quantity altered to numeric(18,3)';
    END IF;
END $$;");

                // 2. Inventory module: Parent table (Products) first
                _invDb.Database.ExecuteSqlRaw(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'StockQuantity' AND data_type <> 'numeric') THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""StockQuantity"" TYPE numeric(18,3);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'ReservedQuantity' AND data_type <> 'numeric') THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""ReservedQuantity"" TYPE numeric(18,3);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'LowStockThreshold' AND data_type <> 'numeric') THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""LowStockThreshold"" TYPE numeric(18,3);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'MinWholesaleQuantity' AND data_type <> 'numeric') THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""MinWholesaleQuantity"" TYPE numeric(18,3);
    END IF;

    -- Child tables: StockMovements, StockReservations
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'QuantityChange' AND data_type <> 'numeric') THEN
        ALTER TABLE ""StockMovements"" ALTER COLUMN ""QuantityChange"" TYPE numeric(18,3);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'NewStockLevel' AND data_type <> 'numeric') THEN
        ALTER TABLE ""StockMovements"" ALTER COLUMN ""NewStockLevel"" TYPE numeric(18,3);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'StockReservations' AND column_name = 'Quantity' AND data_type <> 'numeric') THEN
        ALTER TABLE ""StockReservations"" ALTER COLUMN ""Quantity"" TYPE numeric(18,3);
    END IF;
END $$;");

                // Phase 4: User Hardening, Token Revocation & Role Migration
                _salesDb.Database.ExecuteSqlRaw(@"
                    ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""SecurityStamp"" character varying(64) NOT NULL DEFAULT '';
                    UPDATE ""Users"" SET ""SecurityStamp"" = md5(random()::text || clock_timestamp()::text) WHERE ""SecurityStamp"" = '' OR ""SecurityStamp"" IS NULL;
                    ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""AccessFailedCount"" integer NOT NULL DEFAULT 0;
                    ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""LockoutEndUtc"" timestamp with time zone NULL;
                    ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""LastLoginUtc"" timestamp with time zone NULL;

                    DO $$
                    DECLARE
                        v_has_migrated boolean;
                        v_anomalous_count integer;
                    BEGIN
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
                    END $$;
                ");

                // Upgrade legacy plain-text passwords to PBKDF2 immediately (H-API-23)
                var plainUsers = _salesDb.Users.Where(u => !string.IsNullOrEmpty(u.PasswordHash) && !u.PasswordHash.StartsWith("PBKDF2$")).ToList();
                foreach (var u in plainUsers)
                {
                    u.PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(u.PasswordHash);
                    u.MustChangePassword = true;
                }
                if (plainUsers.Count > 0)
                {
                    _salesDb.SaveChanges();
                    AppLogger.LogStart($"[Security] Upgraded {plainUsers.Count} legacy plain-text password(s) to PBKDF2 with MustChangePassword=true.");
                }

                AppLogger.LogStart("Database column precision verification completed successfully.");
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
            return;
        }

        // 4) Seed del admin: el usuario y la contraseña semilla vienen de la configuración / variables de entorno.
        var seedUsername = !string.IsNullOrWhiteSpace(config["SystemSettings:AdminSeedUsername"])
            ? config["SystemSettings:AdminSeedUsername"]!.Trim()
            : "admin";
        var seedName = !string.IsNullOrWhiteSpace(config["SystemSettings:AdminSeedName"])
            ? config["SystemSettings:AdminSeedName"]!.Trim()
            : "Administrador";
        var seedPassword = config["SystemSettings:AdminSeedPassword"];
        if (string.IsNullOrWhiteSpace(seedPassword))
        {
            var criticalMsg = "[ERROR CRÍTICO] Falta SystemSettings__AdminSeedPassword. " +
                "Establezca la variable de entorno del servicio antes de arrancar.";
            Console.WriteLine(criticalMsg);
            AppLogger.LogDbError(criticalMsg, "Program.SeedPassword");
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            var seedLower = seedUsername.ToLower();
            var targetAdmin = _salesDb.Users.FirstOrDefault(u => 
                u.Username.ToLower() == seedLower || 
                u.Cedula.ToLower() == seedLower ||
                (u.Role == Core.Entities.UserRole.Admin && (u.Username == "V-12345678" || u.Cedula == "V-12345678")));

            if (targetAdmin != null)
            {
                if (string.IsNullOrWhiteSpace(targetAdmin.PasswordHash))
                {
                    targetAdmin.PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(seedPassword);
                    targetAdmin.MustChangePassword = false;
                    AppLogger.LogStart($"[Seed] Set password hash for Admin user: {targetAdmin.Username}");
                    _salesDb.SaveChanges();
                }
            }
            else
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
                    MustChangePassword = false, // Clave elegida explícitamente en el instalador
                    SecurityStamp = Guid.NewGuid().ToString("N")
                };
                _salesDb.Users.Add(newAdmin);
                _salesDb.SaveChanges();
                AppLogger.LogStart($"[Seed] Created customized admin user: {seedUsername} ({seedName})");
            }

            // Ensure ALL Admin users in the system have valid security stamp and password hash if missing (never forcibly reactivate inactive admins)
            var allAdmins = _salesDb.Users.Where(u => u.Role == Core.Entities.UserRole.Admin).ToList();
            bool modifiedAdmins = false;
            foreach (var admin in allAdmins)
            {
                if (string.IsNullOrWhiteSpace(admin.PasswordHash))
                {
                    admin.PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(seedPassword);
                    modifiedAdmins = true;
                    AppLogger.LogStart($"[Seed] Set seed password hash for Admin user: {admin.Username} ({admin.Cedula})");
                }
                if (string.IsNullOrWhiteSpace(admin.SecurityStamp))
                {
                    admin.SecurityStamp = Guid.NewGuid().ToString("N");
                    modifiedAdmins = true;
                }
            }
            if (modifiedAdmins)
            {
                _salesDb.SaveChanges();
            }

            // Add default customer if not exists
            if (!_salesDb.Customers.Any(c => c.IsDefault))
            {
                var existingGeneral = _salesDb.Customers.FirstOrDefault(c => c.CedulaOrRif == "V-00000000");
                if (existingGeneral != null)
                {
                    existingGeneral.IsDefault = true;
                    existingGeneral.Name = "CLIENTE GENERAL / CONSUMIDOR FINAL";
                }
                else
                {
                    _salesDb.Customers.Add(new Core.Entities.Customer
                    {
                        CedulaOrRif = "V-00000000",
                        Name = "CLIENTE GENERAL / CONSUMIDOR FINAL",
                        Phone = "",
                        CreditLimitUSD = 0,
                        IsActive = true,
                        IsDefault = true
                    });
                }
                _salesDb.SaveChanges();
            AppLogger.LogStart("[Seed] Created default Customer.");
            }

            if (!_invDb.Products.Any(p => p.IsCashAdvance))
            {
                _invDb.Products.Add(new Core.Entities.Product
                {
                    Name = "Adelanto de Efectivo",
                    SKU = "ADV-001",
                    Description = "Producto de sistema para operaciones de adelanto de efectivo en caja",
                    PriceRetailUSD = 0m,
                    StockQuantity = 999999,
                    IsCashAdvance = true,
                    IsActive = true
                });
                _invDb.SaveChanges();
                AppLogger.LogStart("[Seed] Created default Cash Advance System Product.");
            }
        }
        catch (System.Exception ex)
        {
            var criticalMsg = "[ERROR CRÍTICO] Error sembrando los datos iniciales. Abortando el arranque. " + ex.Message;
            Console.WriteLine(criticalMsg);
            AppLogger.LogCrash(ex, "SeedDataInitialization");
            Environment.ExitCode = 1;
            return;
        }
    }

    AppLogger.LogStart("Backend API started successfully listening on configured ports.");
    app.Run();
}
catch (Exception fatalEx)
{
    AppLogger.LogCrash(fatalEx, "Backend.API.Program.FatalStartup");
    throw;
}

// Devuelve el certificado HTTPS autofirmado (pos-https.pfx) si está disponible; si no, null.
// Busca primero junto al ejecutable (modo servicio/publicado) y luego en el directorio actual.
X509Certificate2? LoadHttpsCertificate()
{
    const string certPassword = "PosHttpsDev2026!";
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "certs", "pos-https.pfx"),
        Path.Combine(Directory.GetCurrentDirectory(), "certs", "pos-https.pfx"),
    };

    foreach (var candidate in candidates)
    {
        if (File.Exists(candidate))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(candidate, certPassword);
            }
            catch (Exception ex)
            {
                AppLogger.LogStart($"[AVISO] No se pudo cargar el certificado HTTPS ({candidate}): {ex.Message}");
                return null;
            }
        }
    }

    return null;
}
