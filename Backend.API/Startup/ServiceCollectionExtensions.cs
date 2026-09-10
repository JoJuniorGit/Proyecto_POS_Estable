using System;
using System.Linq;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Backend.API.Services;

namespace Backend.API.Startup;

/// <summary>
/// Registra los servicios del backend (DI, EF, JWT, autorización, CORS, rate limiting, etc.).
/// Se extrajo del monolito Program.cs (hallazgo B11) para mantener el punto de entrada legible,
/// facilitar la prueba unitaria del arranque y permitir reutilizar la configuración.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static WebApplicationBuilder AddBackendServices(this WebApplicationBuilder builder, string? connectionString)
    {
        // Persistencia: DbContexts de inventario y ventas con Npgsql + retry y modelo estricto.
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
        builder.Services.AddSingleton<IServiceRestartCoordinator, ServiceRestartCoordinator>();
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
            // 8.30-B03: RequireHttpsMetadata configurable (SecuritySettings:RequireHttpsMetadata,
            // default false por topologia LAN; puede elevarse a true en despliegues solo-HTTPS).
            options.RequireHttpsMetadata = builder.Configuration.GetValue<bool>("SecuritySettings:RequireHttpsMetadata", false);
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
            x.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            x.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        });

        builder.Services.AddOpenApi();
        builder.Services.AddHostedService<Backend.API.Jobs.StockMovementArchiverJob>();
        builder.Services.AddHostedService<Backend.API.Jobs.BcvExchangeRateJob>();
        builder.Services.AddHostedService<Backend.API.Jobs.OutboxProcessorJob>();

        builder.Services.AddSingleton<Backend.API.Services.ChannelReceiptPrintQueue>();
        builder.Services.AddSingleton<Sales.Module.Receipts.IReceiptPrintQueue>(
            sp => sp.GetRequiredService<Backend.API.Services.ChannelReceiptPrintQueue>());
        builder.Services.AddSingleton<Backend.API.Metrics.RequestMetricsRegistry>();
        builder.Services.AddSingleton<Sales.Module.Receipts.IReceiptDocumentRenderer, Sales.Module.Receipts.SaleReceiptRenderer>();
        builder.Services.AddHostedService<Backend.API.Jobs.ReceiptPrintBackgroundService>();

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

        return builder;
    }
}
