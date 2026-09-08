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

    // 8U-M2: archivo de secretos protegido (secrets.json con ACL restrictiva creado por el
    // instalador). Se carga con máxima precedencia (al final de la cadena) y ANTES de
    // LoadHttpsCertificate, evitando exponer credenciales por línea de comandos
    // (AppEnvironmentExtra de NSSM / administrador de tareas).
    try
    {
        var secretsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "secrets.json");
        if (File.Exists(secretsPath))
        {
            builder.Configuration.AddJsonFile(secretsPath, optional: false, reloadOnChange: false);
            AppLogger.LogStart("[8U-M2] Secretos cargados desde secrets.json (ACL restrictiva).");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[STARTUP] Aviso: no se pudo cargar secrets.json: {ex.Message}");
    }

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
        // 8.9-M7: en Producción un arranque sin HTTPS es un fallo de seguridad, no una advertencia.
        if (builder.Environment.IsProduction())
        {
            AppLogger.LogCrash(
                new InvalidOperationException(
                    "[FATAL] HTTPS no disponible en Producción. No se puede iniciar el backend sin canal cifrado. Configure HTTPS_CERT_THUMBPRINT / HTTPS_CERT_PASSWORD o certs/pos-https.pfx."),
                "Backend.API.Program.Startup.HttpsRequired");
            throw new InvalidOperationException(
                "[FATAL] HTTPS no disponible en Producción. El backend se niega a arrancar sin canal cifrado. Consulte el log.");
        }

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
    catch (Exception ex)
    {
        // 8B-B4: no dejar el catch vacío; registrar el fallo del recorrido de directorios.
        Console.WriteLine($"[STARTUP] Aviso: no se pudo recorrer el directorio base en busca de configuracion: {ex.Message}");
    }

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
        options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            // 8.12-L3: PendingModelChangesWarning en modo THROW: si el modelo diverge del
            // snapshot, MigrateAsync y dotnet ef fallan alto en lugar de degradar en silencio.
            // Verificado con `dotnet ef migrations has-pending-model-changes` (sin pendientes).
            .ConfigureWarnings(w => w.Throw(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

    builder.Services.AddDbContext<Sales.Module.Data.SalesDbContext>(options =>
        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            npgsql.EnableRetryOnFailure(3);
        })
            // 8.12-L3: idem InventoryDbContext.
            .ConfigureWarnings(w => w.Throw(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

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
    // 8.14-W4: TTL de IdempotentRequests configurable (appsettings "Idempotency:TtlHours";
    // default 24 h). Permite retención forense de reintentos sin cambios de código.
    var idempotencyTtlHours = builder.Configuration.GetValue<double?>("Idempotency:TtlHours") ?? 24.0;
    builder.Services.AddScoped<Core.Interfaces.IIdempotencyService>(sp =>
        new Sales.Module.Services.IdempotencyService(
            sp.GetRequiredService<Sales.Module.Data.SalesDbContext>(),
            TimeSpan.FromHours(idempotencyTtlHours)));
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
            // 8.9-M6: si el entorno define una lista explícita de orígenes (Cors:AllowedOrigins),
            // se usa esa lista exacta (cero apertura LAN). En Producción es el único modo admisible.
            if (allowedOriginsSet.Count > 0)
            {
                policy.WithOrigins(allowedOriginsSet.ToArray())
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials();
                return;
            }

            policy.SetIsOriginAllowed(origin =>
            {
                if (string.IsNullOrWhiteSpace(origin)) return false;

                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                {
                    var host = uri.Host;
                    int port = uri.Port;
                    bool isAllowedPort = port == 5000 || port == 5001 || port == 5173 || port == 80 || port == 443 || port == 4173;
                    if (!isAllowedPort) return false;

                    // Allow localhost / loopback (gated to allowed POS ports [8W-C1])
                    if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("127.0.0.1") || host.Equals("::1"))
                        return true;

                    // 8.9-M6: en Producción la LAN no se abre por defecto; se exige Cors:AllowedOrigins.
                    if (builder.Environment.IsProduction()) return false;

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
    if (!await Backend.API.Startup.DatabaseInitializer.InitializeAsync(app, connectionString))
    {
        return; // NO seguir arrancando: evita servir peticiones con estado inconsistente
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
        catch (Exception ex)
        {
            // 8B-B4: catch vacío convertido en aviso; se continúa con las SAN ya acumuladas.
            Console.WriteLine($"[HTTPS] Aviso: no se pudieron enumerar las IPs para el certificado autofirmado: {ex.Message}");
        }

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
