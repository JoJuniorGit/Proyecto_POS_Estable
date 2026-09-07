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
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using Logistics.Module.Extensions;
using Microsoft.AspNetCore.HttpOverrides;

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
    // Intenta cargar el certificado HTTPS autofirmado para escuchar también en el puerto 5001.
    // Si el certificado falta, el servidor continúa sirviendo solo HTTP sin romper el arranque.
    var httpsCert = LoadHttpsCertificate(builder.Configuration, builder.Environment);

    // Limpia la configuración de 'urls' para evitar la advertencia de Kestrel (Overriding address(es)) al definir ListenAnyIP.
    builder.Configuration["urls"] = null;

    // 8.7-L2: AllowedHosts restringido — sin "*". Se permite localhost, el nombre del equipo y las
    // IPs locales de la máquina (LAN POS), preservando entradas explícitas de configuración/env.
    var rawAllowedHosts = builder.Configuration["AllowedHosts"] ?? "localhost";
    var computedAllowedHosts = string.IsNullOrWhiteSpace(rawAllowedHosts) || rawAllowedHosts == "*"
        ? BuildLanAllowedHosts()
        : BuildLanAllowedHosts() + ";" + rawAllowedHosts;
    builder.Configuration["AllowedHosts"] = computedAllowedHosts;

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
        options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3)));
    // 8.7-B8: la advertencia de cambios pendientes del modelo se conserva ACTIVA: cualquier
    // divergencia entre modelo/migraciones debe ser visible y resolverse con una migración EF,
    // no con SQL crudo inline.

    builder.Services.AddDbContext<Sales.Module.Data.SalesDbContext>(options =>
        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            npgsql.EnableRetryOnFailure(3);
        }));
    // 8.7-B8: PendingModelChangesWarning ACTIVA (ver InventoryDbContext arriba).

    builder.Services.AddMemoryCache(options =>
    {
        options.SizeLimit = builder.Configuration.GetValue<int>("MemoryCache:SizeLimit", 10000);
    });
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
    builder.Services.AddScoped<ITokenService, TokenService>();
    builder.Services.AddScoped<ISecurityStampValidator, SecurityStampValidator>();
    var customPasswordBlacklist = builder.Configuration.GetSection("SecuritySettings:PasswordBlacklist").Get<string[]>();
    builder.Services.AddSingleton<Core.Interfaces.IPasswordPolicyService>(new Core.Services.PasswordPolicyService(customPasswordBlacklist));
    builder.Services.AddSingleton<INetworkDiscoveryService, NetworkDiscoveryService>();
    builder.Services.AddScoped<IInventoryService, InventoryService>();
    builder.Services.AddScoped<ISystemSettingsService, SystemSettingsService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.ISalesService, Sales.Module.Services.SalesService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.ICashDrawerService, Sales.Module.Services.CashDrawerService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.IPaymentMethodService, Sales.Module.Services.PaymentMethodService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.IPaymentMethodNotifier, Backend.API.Services.SignalRPaymentMethodNotifier>();
    builder.Services.AddScoped<Sales.Module.Interfaces.IDailyClosureService, Sales.Module.Services.DailyClosureService>();
    builder.Services.AddScoped<Core.Interfaces.IIdempotencyService, Sales.Module.Services.IdempotencyService>();
    builder.Services.AddScoped<Backend.API.Services.IExchangeRateWriteService, Backend.API.Services.ExchangeRateWriteService>();
    builder.Services.AddHostedService<Backend.API.Jobs.IdempotencyCleanupJob>();
    builder.Services.AddHostedService<Backend.API.Jobs.ReservationExpiryJob>();

    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(typeof(Sales.Module.Services.SalesService).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(Inventory.Module.Services.InventoryService).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(Logistics.Module.Services.DeliveryService).Assembly);
    });
    // Logistics.Module (Clasificado formalmente como NO-PRODUCCIÓN / EXPERIMENTAL - almacenamiento en memoria [8L-CR3])
    builder.Services.AddLogisticsModule();

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
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                else if (string.IsNullOrEmpty(context.Token))
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
                    int port = uri.Port;
                    bool isAllowedPort = port == 5000 || port == 5001 || port == 5173 || port == 80 || port == 443 || port == 4173;
                    if (!isAllowedPort) return false;

                    // Allow localhost / loopback (gated to allowed POS ports [8W-C1])
                    if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("127.0.0.1") || host.Equals("::1"))
                        return true;

                    // Allow Private Intranet Subnets (RFC-1918) for POS LAN network (gated to POS application ports: 5000, 5001, 5173)
                    if (System.Net.IPAddress.TryParse(host, out var ip))
                    {
                        var bytes = ip.GetAddressBytes();
                        if (bytes.Length == 4)
                        {
                            if (bytes[0] == 10) return true; // 10.0.0.0/8
                            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true; // 172.16.0.0/12
                            if (bytes[0] == 192 && bytes[1] == 168) return true; // 192.168.0.0/16
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

    // Forwarded Headers Configuration (SEC-10)
    // Permite normalizar X-Forwarded-For y X-Forwarded-Proto cuando el backend opera tras un reverse proxy (Nginx, IIS, Caddy).
    // Por defecto en ASP.NET Core, confía en proxies en loopback (127.0.0.1/8 y ::1/128).
    // Permite además extender KnownProxies y KnownNetworks mediante appsettings.json si se despliega tras proxies remotos.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        var knownProxiesConfig = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>();
        if (knownProxiesConfig != null)
        {
            foreach (var proxy in knownProxiesConfig)
            {
                if (System.Net.IPAddress.TryParse(proxy, out var ip))
                {
                    options.KnownProxies.Add(ip);
                }
            }
        }

        var knownNetworksConfig = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>();
        if (knownNetworksConfig != null)
        {
            foreach (var net in knownNetworksConfig)
            {
                if (System.Net.IPNetwork.TryParse(net, out var parsedNet))
                {
                    options.KnownIPNetworks.Add(parsedNet);
                }
            }
        }
    });

    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
        options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    });

    var app = builder.Build();

    // 8.7-L2: validación del encabezado Host contra la whitelist calculada (AllowedHosts).
    // Debe ejecutarse antes del manejo de encabezados reenviados para no confiar en Host externos.
    app.UseHostFiltering();

    // Normalizar encabezados reenviados (X-Forwarded-For / X-Forwarded-Proto) al inicio del pipeline
    app.UseForwardedHeaders();

    // Global Unhandled Exception & DB Resilience Middleware
    app.UseMiddleware<Backend.API.Middleware.GlobalExceptionHandlerMiddleware>();

    // Security Headers (M-07)
    app.UseMiddleware<Backend.API.Middleware.SecurityHeadersMiddleware>();

    // 8.7-B10: Con certificado presente, en Producción se fuerza HTTPS para el cliente Web:
    //  - HSTS dirige al browser a HTTPS desde la primera respuesta segura.
    //  - Redirección 307 HTTP→HTTPS para request con X-Client-Platform: Web (preserva el body
    //    del login). El escritorio (desktop) sigue sobre HTTP en LAN por compatibilidad, pero la
    //    cookie pos_jwt es Secure siempre (solo viaja por HTTPS).
    if (httpsCert != null && !app.Environment.IsDevelopment())
    {
        app.UseHsts();
        app.Use(async (context, next) =>
        {
            var platform = context.Request.Headers["X-Client-Platform"].ToString();
            if (!context.Request.IsHttps
                && string.Equals(platform, "Web", StringComparison.OrdinalIgnoreCase))
            {
                var host = context.Request.Host.Host;
                var target = $"https://{host}:{httpsPort}{context.Request.Path}{context.Request.QueryString}";
                context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = target;
                return;
            }
            await next();
        });
    }

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
    app.UseMiddleware<Backend.API.Middleware.MustChangePasswordMiddleware>();

    app.MapControllers().RequireRateLimiting("GeneralApiRateLimit");
    app.MapHub<ExchangeRateHub>("/hubs/exchange-rate").RequireAuthorization();

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
            AppLogger.LogStart("Running EF Core Database Migrations asynchronously...");
            await _invDb.Database.MigrateAsync();
            await _salesDb.Database.MigrateAsync();

            // 8.7-B8 (segregación): bloque de CONVERGENCIA LEGACY idempotente (information_schema),
            // solo alcanza a BD instaladas antes de estas migraciones EF. En instalaciones nuevas el
            // esquema proviene 100% de las migraciones; la advertencia PendingModelChangesWarning
            // ahora está activa para detectar cualquier divergencia futura del modelo.
            // CONSERVAR: no agregar más ALTER TABLE inline aquí — toda evolución de esquema debe ser una
            // migración EF (dotnet ef migrations add).
            try
            {
                AppLogger.LogStart("Verifying and adjusting database schema and column precision (numeric 18,3)...");

                await _salesDb.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'PaymentMethods') THEN
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'PaymentMethods' AND column_name = 'DisplayOrder') THEN
            ALTER TABLE ""PaymentMethods"" ADD COLUMN ""DisplayOrder"" integer NOT NULL DEFAULT 0;
        END IF;
    END IF;
END $$;");

                await _salesDb.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'CashTransactions') THEN
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'CashTransactions' AND column_name = 'IsPhysicalCash') THEN
            ALTER TABLE ""CashTransactions"" ADD COLUMN ""IsPhysicalCash"" boolean NOT NULL DEFAULT true;
        END IF;
    END IF;
END $$;");

                await _salesDb.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Sales') THEN
        CREATE SEQUENCE IF NOT EXISTS factura_number_seq START WITH 1 INCREMENT BY 1 NO MINVALUE NO MAXVALUE CACHE 1;
        PERFORM setval('factura_number_seq', GREATEST(COALESCE((SELECT MAX(""InvoiceNumber"") FROM ""Sales""), 0) + 1, 1), false);
    END IF;
END $$;");

                // 1. Sales module: SaleItems.Quantity -> numeric(18,3)
                await _salesDb.Database.ExecuteSqlRawAsync(@"
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
                await _invDb.Database.ExecuteSqlRawAsync(@"
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

                // Phase 4: User Hardening, Token Revocation & Role Migration (Defensive with information_schema checks)
                await _salesDb.Database.ExecuteSqlRawAsync(@"
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

                await _salesDb.Database.ExecuteSqlRawAsync(@"
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
                var usersTableExists = (await _salesDb.Database.SqlQueryRaw<int>(
                    @"SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Users'"
                ).ToListAsync()).Any();

                if (usersTableExists)
                {
                    var plainUsers = await _salesDb.Users
                        .Where(u => !string.IsNullOrEmpty(u.PasswordHash) && !u.PasswordHash.StartsWith("PBKDF2$"))
                        .ToListAsync();
                    foreach (var u in plainUsers)
                    {
                        u.PasswordHash = Backend.API.Services.PasswordHasher.HashPassword(u.PasswordHash);
                        u.MustChangePassword = true;
                    }
                    if (plainUsers.Count > 0)
                    {
                        await _salesDb.SaveChangesAsync();
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
                return;
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
                    return;
                }
            }

            var seedLower = seedUsername.ToLower();
            var targetAdmin = hasSeedPassword ? _salesDb.Users.FirstOrDefault(u => 
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
                    _salesDb.SaveChanges();
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
                _salesDb.Users.Add(newAdmin);
                _salesDb.SaveChanges();
                AppLogger.LogStart($"[Seed] Created customized admin user: {seedUsername} ({seedName})");
            }

            // Ensure ALL Admin users in the system have valid security stamp and password hash if missing (never forcibly reactivate inactive admins)
            if (hasSeedPassword)
            {
            var allAdmins = _salesDb.Users.Where(u => u.Role == Core.Entities.UserRole.Admin).ToList();
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
                _salesDb.SaveChanges();
            }
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

// Devuelve el certificado HTTPS si está disponible (vía Windows Certificate Store o pos-https.pfx); si no, null.
// Nunca usa contraseñas hardcodeadas en producción; resuelve desde Store o HTTPS_CERT_PASSWORD.
X509Certificate2? LoadHttpsCertificate(IConfiguration config, IHostEnvironment env)
{
    // 1. Prioridad: Windows Certificate Store (Recomendado en Windows Server / Entornos Corporativos)
    var thumbprint = (Environment.GetEnvironmentVariable("HTTPS_CERT_THUMBPRINT") 
                      ?? config["SystemSettings:HttpsCertThumbprint"] 
                      ?? config["Kestrel:Certificates:Default:Subject"])?.Replace(" ", "").ToUpperInvariant();

    if (!string.IsNullOrWhiteSpace(thumbprint))
    {
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                if (matches.Count > 0)
                {
                    AppLogger.LogStart($"[HTTPS] Certificado cargado exitosamente desde Windows Certificate Store ({location}): {matches[0].Subject}");
                    return matches[0];
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogStart($"[HTTPS] [AVISO] Error al consultar Windows Certificate Store ({location}): {ex.Message}");
            }
        }
    }

    // 2. Archivo .pfx local en directorio certs/
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "certs", "pos-https.pfx"),
        Path.Combine(Directory.GetCurrentDirectory(), "certs", "pos-https.pfx"),
    };

    foreach (var candidate in candidates)
    {
        if (File.Exists(candidate))
        {
            string certPassword = Environment.GetEnvironmentVariable("HTTPS_CERT_PASSWORD")
                                ?? config["Kestrel:Certificates:Default:Password"]
                                ?? string.Empty;

            try
            {
                var cert = X509CertificateLoader.LoadPkcs12FromFile(candidate, certPassword);
                AppLogger.LogStart($"[HTTPS] Certificado HTTPS cargado exitosamente desde archivo ({candidate}): {cert.Subject}");
                return cert;
            }
            catch (Exception ex)
            {
                AppLogger.LogStart($"[HTTPS] [AVISO] No se pudo cargar el certificado HTTPS ({candidate}): {ex.Message}");
            }
        }
    }

    // 3. Fallback dinámico: Generar certificado autofirmado en memoria para asegurar disponibilidad de HTTPS
    try
    {
        using var rsa = RSA.Create(2048);
        var certRequest = new CertificateRequest(
            $"CN={Environment.MachineName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        certRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        certRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName(Environment.MachineName);
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    sanBuilder.AddIpAddress(ip);
                }
            }
        }
        catch { }

        certRequest.CertificateExtensions.Add(sanBuilder.Build());

        var ephemeralCert = certRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));

        var pfxBytes = ephemeralCert.Export(X509ContentType.Pfx);
        var certWithKey = X509CertificateLoader.LoadPkcs12(pfxBytes, null);
        AppLogger.LogStart($"[HTTPS] Certificado autofirmado generado dinámicamente en memoria para {Environment.MachineName} (SANs configurados para LAN).");
        return certWithKey;
    }
    catch (Exception ex)
    {
        AppLogger.LogCrash(ex, "[HTTPS] Fallo al generar certificado autofirmado en memoria");
    }

    return null;
}

/// <summary>8.7-L2: devuelve la whitelist de hosts válidos para el filtro de host de ASP.NET
/// (localhost + nombre del equipo + IPs locales), permitiendo el acceso LAN sin "*".</summary>
static string BuildLanAllowedHosts()
{
    var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "::1", "[::1]"
    };

    try
    {
        string hostName = Dns.GetHostName();
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            hosts.Add(hostName);
        }

        foreach (var ip in Dns.GetHostAddresses(hostName))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                hosts.Add(ip.ToString());
            }
            else if (ip.IsIPv6LinkLocal)
            {
                hosts.Add(ip.ToString());
            }
        }

        // 8.6-M9/L2: enumera adaptadores reales para cubrir APIPA/link-local (169.254/16 y fe80::)
        // que DNS suele omitir: en LAN sin DHCP el host del reenvío sería 169.254.x.x y la
        // whitelist anterior lo rechazaría, rompiendo el Pairing del POS.
        foreach (var networkInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    hosts.Add(address.ToString());
                }
                else if (address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal)
                {
                    hosts.Add(address.ToString());
                }
            }
        }
    }
    catch
    {
        // Sin DNS/disponibilidad de red: se mantiene la base localhost.
    }

    return string.Join(";", hosts);
}

// 8.6-B6: expone Program para WebApplicationFactory<Program> (clase pública requerida por el
// integrador de hosting de pruebas sin reescribir el entrypoint de producción).
public partial class Program { }
