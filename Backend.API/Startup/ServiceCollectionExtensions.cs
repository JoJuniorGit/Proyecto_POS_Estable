using System;
using System.Linq;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Backend.API.Services;

namespace Backend.API.Startup;

public static class ServiceCollectionExtensions
{
    public static WebApplicationBuilder AddBackendServices(this WebApplicationBuilder builder, string? connectionString)
    {
        builder.Services.AddDbContext<InventoryDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
                .ConfigureWarnings(w => w.Throw(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        builder.Services.AddDbContext<Sales.Module.Data.SalesDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                npgsql.EnableRetryOnFailure(3);
            })
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
        builder.Services.AddScoped<IProductManagementService>(sp => (InventoryService)sp.GetRequiredService<IInventoryService>());
        builder.Services.AddScoped<IReservationService, Inventory.Module.Services.ReservationService>();
        builder.Services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        builder.Services.AddScoped<ITimeZoneProvider, Core.Services.TimeZoneProvider>();
        builder.Services.AddScoped<IExchangeRateHistoryService, ExchangeRateHistoryService>();
        builder.Services.AddScoped<IUserService, Sales.Module.Services.UserService>();
        builder.Services.AddScoped<Core.Interfaces.IAuthService, Sales.Module.Services.AuthService>();
        builder.Services.AddScoped<Sales.Module.Interfaces.ISalesService, Sales.Module.Services.SalesService>();
        builder.Services.AddScoped<Sales.Module.Interfaces.ICashDrawerService, Sales.Module.Services.CashDrawerService>();
        builder.Services.AddScoped<Sales.Module.Services.CashAdvanceCoordinator>();
        builder.Services.AddScoped<Sales.Module.Interfaces.IPaymentMethodService, Sales.Module.Services.PaymentMethodService>();
        builder.Services.AddScoped<Sales.Module.Interfaces.IPaymentMethodNotifier, Backend.API.Services.SignalRPaymentMethodNotifier>();
        builder.Services.AddScoped<Sales.Module.Interfaces.IHoldOrderNotifier, Backend.API.Services.SignalRHoldOrderNotifier>();
        builder.Services.AddScoped<Sales.Module.Interfaces.IDailyClosureService, Sales.Module.Services.DailyClosureService>();
        builder.Services.AddScoped<Core.Interfaces.ITodayExchangeRateProvider, Backend.API.Services.TodayExchangeRateProvider>();
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

        builder.Services.AddHttpClient<BcvScraperService>();
        builder.Services.AddHostedService<Backend.API.Services.CacheMetricsLoggerService>();
        builder.Services.AddSignalR();

        builder.Services.Configure<Core.Configuration.SystemSettingsOptions>(builder.Configuration.GetSection(Core.Configuration.SystemSettingsOptions.SectionName));

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
        builder.Services.AddScoped<Sales.Module.Receipts.ISalesReceiptService, Sales.Module.Receipts.SalesReceiptService>();
        builder.Services.AddHostedService<Backend.API.Jobs.ReceiptPrintBackgroundService>();

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

            var generalApiPermit = Math.Max(1, builder.Configuration.GetValue<int>("RateLimiting:GeneralApiRateLimit", 200));

            options.AddPolicy("GeneralApiRateLimit", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = generalApiPermit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));
        });

        var allowedOriginsConfig = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        var allowedOriginsSet = new HashSet<string>(allowedOriginsConfig, StringComparer.OrdinalIgnoreCase);

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
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

                        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("127.0.0.1") || host.Equals("::1"))
                            return true;

                        if (!builder.Environment.IsDevelopment()) return false;

                        if (System.Net.IPAddress.TryParse(host, out var ip))
                        {
                            var bytes = ip.GetAddressBytes();
                            if (bytes.Length == 4)
                            {
                                if (bytes[0] == 10) return true;
                                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                                if (bytes[0] == 192 && bytes[1] == 168) return true;
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
