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

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(5000);
        if (httpsCert != null)
        {
            kestrel.ListenAnyIP(5001, listen => listen.UseHttps(httpsCert));
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

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
    builder.Services.AddScoped<IInventoryService, InventoryService>();
    builder.Services.AddScoped<ISystemSettingsService, SystemSettingsService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.ISalesService, Sales.Module.Services.SalesService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.ICashDrawerService, Sales.Module.Services.CashDrawerService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.IPaymentMethodService, Sales.Module.Services.PaymentMethodService>();
    builder.Services.AddScoped<Sales.Module.Interfaces.IDailyClosureService, Sales.Module.Services.DailyClosureService>();

    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(typeof(Sales.Module.Services.SalesService).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(Inventory.Module.Services.InventoryService).Assembly);
    });

    // BCV Services
    builder.Services.AddHttpClient<BcvScraperService>();
    builder.Services.AddHostedService<Backend.API.Jobs.BcvExchangeRateJob>();
    builder.Services.AddSignalR();

    builder.Services.Configure<Core.Configuration.SystemSettingsOptions>(builder.Configuration.GetSection(Core.Configuration.SystemSettingsOptions.SectionName));

    builder.Services.AddControllers().AddJsonOptions(x =>
    {
        x.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

    builder.Services.AddOpenApi();
    builder.Services.AddHostedService<Backend.API.Jobs.StockMovementArchiverJob>();

    // CORS: permitir acceso desde cualquier dispositivo en la red local (incluyendo SignalR con credenciales)
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.SetIsOriginAllowed(_ => true)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        });
    });


    var app = builder.Build();

    // Global Unhandled Exception & DB Resilience Middleware
    app.UseMiddleware<Backend.API.Middleware.GlobalExceptionHandlerMiddleware>();

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

    // Version Compatibility Handshake Middleware
    app.UseMiddleware<Backend.API.Middleware.VersionCheckMiddleware>();

    app.MapControllers();
    app.MapHub<ExchangeRateHub>("/hubs/exchange-rate");

    // Ensure Seed Data and Migrations
    using (var scope = app.Services.CreateScope())
    {
        var _salesDb = scope.ServiceProvider.GetRequiredService<Sales.Module.Data.SalesDbContext>();
        var _invDb = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var systemSettingsOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Configuration.SystemSettingsOptions>>().Value;

        bool isDbConnected = false;
        try
        {
            AppLogger.LogStart("Testing PostgreSQL Database Connection (CanConnectAsync)...");
            isDbConnected = _invDb.Database.CanConnect();
            if (!isDbConnected)
            {
                var criticalMsg = "[ERROR CRÍTICO] Las credenciales de PostgreSQL en appsettings.json son incorrectas. La aplicación no puede comunicarse con la base de datos.";
                Console.WriteLine(criticalMsg);
                AppLogger.LogDbError(criticalMsg, "Program.CanConnect");
            }
            else
            {
                AppLogger.LogStart("PostgreSQL Database Connection successful.");
            }
        }
        catch (System.Exception connEx)
        {
            var criticalMsg = $"[ERROR CRÍTICO] Las credenciales de PostgreSQL en appsettings.json son incorrectas o el servicio no responde: {connEx.Message}";
            Console.WriteLine(criticalMsg);
            AppLogger.LogDbError(connEx, "Program.CanConnectException");
        }

        if (isDbConnected)
        {
            try
            {
                AppLogger.LogStart("Running EF Core Database Migrations...");
                _invDb.Database.Migrate();
                _salesDb.Database.Migrate();

                // Defensive schema check: Ensure DisplayOrder column exists in PaymentMethods table
                try
                {
                    _salesDb.Database.ExecuteSqlRaw(@"ALTER TABLE ""PaymentMethods"" ADD COLUMN IF NOT EXISTS ""DisplayOrder"" integer NOT NULL DEFAULT 0;");
                    _salesDb.Database.ExecuteSqlRaw(@"ALTER TABLE ""CashTransactions"" ADD COLUMN IF NOT EXISTS ""IsPhysicalCash"" boolean NOT NULL DEFAULT true;");
                }
                catch { }

                AppLogger.LogStart("EF Core Database Migrations applied successfully.");
            }
            catch (System.Exception ex)
            {
                AppLogger.LogDbError(ex, "Database.Migrate");
                Console.WriteLine("Error running database migrations: " + ex.Message);
            }

        try
        {
            // Ensure user V-12345678, V-00000000 or Admin username is ALWAYS active on startup
            var targetUser123 = _salesDb.Users.FirstOrDefault(u => u.Cedula == "V-12345678");
            if (targetUser123 != null)
            {
                if (!targetUser123.IsActive)
                {
                    targetUser123.IsActive = true;
                    _salesDb.SaveChanges();
                    AppLogger.LogStart("[Seed] Reactivated user V-12345678");
                }
            }
            else
            {
                var user123 = new Core.Entities.User
                {
                    Cedula = "V-12345678",
                    Name = "Administrador",
                    Username = "V-12345678",
                    FullName = "Administrador",
                    PasswordHash = systemSettingsOptions.AdminSeedPassword,
                    Role = Core.Entities.UserRole.Admin,
                    IsActive = true
                };
                _salesDb.Users.Add(user123);
                _salesDb.SaveChanges();
                AppLogger.LogStart("[Seed] Created and activated user V-12345678");
            }

            // Ensure ALL Admin users in the system are ALWAYS active on startup
            var disabledAdmins = _salesDb.Users.Where(u => u.Role == Core.Entities.UserRole.Admin && !u.IsActive).ToList();
            if (disabledAdmins.Any())
            {
                foreach (var admin in disabledAdmins)
                {
                    admin.IsActive = true;
                    AppLogger.LogStart($"[Seed] Reactivated Admin user: {admin.Username} ({admin.Cedula})");
                }
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
            AppLogger.LogCrash(ex, "SeedDataInitialization");
            Console.WriteLine("Error seeding initial data: " + ex.Message);
        }
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
