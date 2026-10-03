using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Integration;

[Collection(PostgresRealCollection.Name)]
public class SupplierInvoiceApplyIntegrationTests
{
    [Fact]
    public async Task ConfirmAsync_RejectsForgedNegativeMarginOverrideWithoutPersistingProductChanges()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        await using var connectionScope = connection;
        await using var contextScope = context;
        var product = CreateProduct("APPLY-FORGED", 4m, 6m, 20m);
        var invoice = await AddInvoiceAsync(context, CreateLine(product, 2m, 11m));
        var line = Assert.Single(invoice.Lines);
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(new[]
            {
                new ConfirmLineDto(line.Id, true, -1m, null)
            })));

        await using var verificationContext = CreateSqliteInventoryDbContext(connection);
        var savedProduct = await verificationContext.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await verificationContext.SupplierInvoices.AsNoTracking().SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(4m, savedProduct.CostPriceUSD);
        Assert.Equal(20m, savedProduct.ProfitMarginRetail);
        Assert.Equal(6m, savedProduct.StockQuantity);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
        Assert.Empty(await verificationContext.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ConfirmAsync_RejectsNegativePersistedUnitCost()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        await using var connectionScope = connection;
        await using var contextScope = context;
        var product = CreateProduct("APPLY-NEGATIVE-COST", 4m, 6m, 20m);
        var invoice = await AddInvoiceAsync(context, CreateLine(product, 2m, -1m));
        var line = Assert.Single(invoice.Lines);
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(new[]
            {
                new ConfirmLineDto(line.Id, true, null, null)
            })));

        await using var verificationContext = CreateSqliteInventoryDbContext(connection);
        var savedProduct = await verificationContext.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        Assert.Equal(4m, savedProduct.CostPriceUSD);
        Assert.Equal(20m, savedProduct.ProfitMarginRetail);
        Assert.Equal(6m, savedProduct.StockQuantity);
        Assert.Empty(await verificationContext.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ConfirmAsync_RejectsSoftDeletedResolvedProduct()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        await using var connectionScope = connection;
        await using var contextScope = context;
        var product = CreateProduct("APPLY-DELETED", 4m, 6m, 20m);
        product.IsDeleted = true;
        var invoice = await AddInvoiceAsync(context, CreateLine(product, 2m, 11m));
        var line = Assert.Single(invoice.Lines);
        var service = CreateService(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(new[]
            {
                new ConfirmLineDto(line.Id, true, null, null)
            })));

        await using var verificationContext = CreateSqliteInventoryDbContext(connection);
        var savedProduct = await verificationContext.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await verificationContext.SupplierInvoices.AsNoTracking().SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.True(savedProduct.IsDeleted);
        Assert.Equal(4m, savedProduct.CostPriceUSD);
        Assert.Equal(6m, savedProduct.StockQuantity);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
        Assert.Empty(await verificationContext.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ConfirmAsync_RollsBackEarlierApprovedLineWhenLaterLineFailsValidation()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        await using var connectionScope = connection;
        await using var contextScope = context;
        var firstProduct = CreateProduct("APPLY-ROLLBACK-FIRST", 4m, 6m, 20m);
        var invalidProduct = CreateProduct("APPLY-ROLLBACK-INVALID", 7m, 8m, 30m);
        var invoice = await AddInvoiceAsync(
            context,
            CreateLine(firstProduct, 2m, 11m),
            CreateLine(invalidProduct, -1m, 13m));
        var lines = invoice.Lines.OrderBy(line => line.Id).ToArray();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(new[]
            {
                new ConfirmLineDto(lines[0].Id, true, null, null),
                new ConfirmLineDto(lines[1].Id, true, null, null)
            })));

        await using var verificationContext = CreateSqliteInventoryDbContext(connection);
        var savedFirst = await verificationContext.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == firstProduct.Id);
        var savedInvalid = await verificationContext.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == invalidProduct.Id);
        var savedInvoice = await verificationContext.SupplierInvoices.AsNoTracking().SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(4m, savedFirst.CostPriceUSD);
        Assert.Equal(6m, savedFirst.StockQuantity);
        Assert.Equal(7m, savedInvalid.CostPriceUSD);
        Assert.Equal(8m, savedInvalid.StockQuantity);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
        Assert.Empty(await verificationContext.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ConfirmEndpoint_CashierReceivesForbiddenWithoutApplyingInvoice()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        await using var connectionScope = connection;
        await using var contextScope = context;
        var product = CreateProduct("APPLY-CASHIER", 4m, 6m, 20m);
        var invoice = await AddInvoiceAsync(context, CreateLine(product, 2m, 11m));
        var service = CreateService(context);
        await using var application = await CreateAuthorizationApplicationAsync(service);
        using var client = application.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "Cashier");

        using var response = await client.PostAsJsonAsync(
            $"/api/supplier-invoices/{invoice.Id}/confirm",
            new ConfirmSupplierInvoiceRequestDto(new[]
            {
                new ConfirmLineDto(Assert.Single(invoice.Lines).Id, true, null, null)
            }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verificationContext = CreateSqliteInventoryDbContext(connection);
        var savedProduct = await verificationContext.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await verificationContext.SupplierInvoices.AsNoTracking().SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(4m, savedProduct.CostPriceUSD);
        Assert.Equal(6m, savedProduct.StockQuantity);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
        Assert.Empty(await verificationContext.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ConfirmAsync_ReloadsProductAndRetriesXminConflict_WhenPostgresIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await TestSchemaBootstrap.EnsureSharedSchemaAsync(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var sku = $"APPLY-CONC-{suffix}";
        var normalizedSupplierName = $"APPLY CONCURRENCY SUPPLIER {suffix}";
        var invoiceId = 0;
        var productId = 0;

        try
        {
            await using var context = CreatePostgresContext(connectionString);
            var product = CreateProduct(sku, 4m, 6m, 20m);
            var invoice = await AddInvoiceAsync(context, normalizedSupplierName, CreateLine(product, 2m, 11m));
            var line = Assert.Single(invoice.Lines);
            invoiceId = invoice.Id;
            productId = product.Id;

            var concurrentUpdateCount = 0;
            var currentUser = new Mock<ICurrentUserService>();
            currentUser.SetupGet(user => user.UserId).Returns(() =>
            {
                if (Interlocked.Exchange(ref concurrentUpdateCount, 1) == 0)
                {
                    using var concurrentContext = CreatePostgresContext(connectionString);
                    var concurrentProduct = concurrentContext.Products.Single(candidate => candidate.Id == productId);
                    concurrentProduct.Description = "Concurrent catalog edit";
                    concurrentContext.SaveChanges();
                }

                return "supplier-invoice-concurrency-user";
            });
            var service = CreateService(context, currentUser.Object);

            var result = await service.ConfirmAsync(invoiceId, new ConfirmSupplierInvoiceRequestDto(new[]
            {
                new ConfirmLineDto(line.Id, true, null, null)
            }));

            Assert.Equal(nameof(SupplierInvoiceStatus.Applied), result.Status);
            Assert.Equal(1, concurrentUpdateCount);
            await using var verificationContext = CreatePostgresContext(connectionString);
            var savedProduct = await verificationContext.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == productId);
            Assert.Equal("Concurrent catalog edit", savedProduct.Description);
            Assert.Equal(11m, savedProduct.CostPriceUSD);
            Assert.Equal(8m, savedProduct.StockQuantity);
            var movement = await verificationContext.StockMovements.AsNoTracking().SingleAsync(candidate => candidate.ProductId == productId);
            Assert.Equal("supplier-invoice-concurrency-user", movement.UserId);
        }
        finally
        {
            await RemovePostgresTestRowsAsync(connectionString, sku, normalizedSupplierName, productId, invoiceId);
        }
    }

    private static SupplierInvoiceService CreateService(
        InventoryDbContext context,
        ICurrentUserService? currentUser = null)
    {
        var settings = Mock.Of<ISystemSettingsService>();
        var similaritySearch = Mock.Of<ISupplierProductSimilaritySearch>();
        currentUser ??= Mock.Of<ICurrentUserService>();
        return new SupplierInvoiceService(context, settings, similaritySearch, currentUser);
    }

    private static async Task<WebApplication> CreateAuthorizationApplicationAsync(ISupplierInvoiceService service)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(Backend.API.Controllers.SupplierInvoicesController).Assembly);
        builder.Services.AddAuthentication("TestAuth")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("TestAuth", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ISupplierInvoiceService>(service);

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapControllers();
        await application.StartAsync();
        return application;
    }

    private static async Task<SupplierInvoice> AddInvoiceAsync(
        InventoryDbContext context,
        params SupplierInvoiceLine[] lines) => await AddInvoiceAsync(context, null, lines);

    private static async Task<SupplierInvoice> AddInvoiceAsync(
        InventoryDbContext context,
        string? normalizedSupplierName,
        params SupplierInvoiceLine[] lines)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var supplier = new Supplier
        {
            CommercialName = $"Apply Integration Supplier {suffix}",
            NormalizedCommercialName = normalizedSupplierName ?? $"APPLY INTEGRATION SUPPLIER {suffix}"
        };
        var invoice = new SupplierInvoice
        {
            Supplier = supplier,
            Status = SupplierInvoiceStatus.Draft,
            Lines = new List<SupplierInvoiceLine>(lines)
        };

        context.SupplierInvoices.Add(invoice);
        await context.SaveChangesAsync();
        return invoice;
    }

    private static Product CreateProduct(string sku, decimal cost, decimal stock, decimal retailMargin) => new()
    {
        Name = sku,
        SKU = sku,
        CostPriceUSD = cost,
        Cost = 91m,
        ProfitMarginRetail = retailMargin,
        ProfitMarginWholesale = retailMargin,
        ProfitPercentage = retailMargin,
        PriceUSD = 20m,
        PriceRetailUSD = 20m,
        PriceWholesaleUSD = 18m,
        PriceBsS = 123.45m,
        LastConversionRate = 36.5m,
        StockQuantity = stock
    };

    private static SupplierInvoiceLine CreateLine(
        Product product,
        decimal quantity,
        decimal unitCost) => new()
    {
        Quantity = quantity,
        UnitCostUSD = unitCost,
        Status = SupplierInvoiceLineStatus.Update,
        ResolvedProduct = product,
        MatchMethod = MatchMethod.Barcode
    };

    private static InventoryDbContext CreatePostgresContext(string connectionString) => new(
        new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(connectionString, postgres => postgres.EnableRetryOnFailure(3))
            .Options);

    private static InventoryDbContext CreateSqliteInventoryDbContext(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(connection)
            .Options);

    private static async Task RemovePostgresTestRowsAsync(
        string connectionString,
        string sku,
        string normalizedSupplierName,
        int productId,
        int invoiceId)
    {
        await using var context = CreatePostgresContext(connectionString);
        var supplier = await context.Suppliers.FirstOrDefaultAsync(candidate => candidate.NormalizedCommercialName == normalizedSupplierName);
        var invoice = invoiceId == 0
            ? supplier is null
                ? null
                : await context.SupplierInvoices.Include(candidate => candidate.Lines)
                    .FirstOrDefaultAsync(candidate => candidate.SupplierId == supplier.Id)
            : await context.SupplierInvoices.Include(candidate => candidate.Lines)
                .FirstOrDefaultAsync(candidate => candidate.Id == invoiceId);
        if (invoice is not null)
        {
            context.SupplierInvoices.Remove(invoice);
        }

        var product = productId == 0
            ? await context.Products.FirstOrDefaultAsync(candidate => candidate.SKU == sku)
            : await context.Products.FirstOrDefaultAsync(candidate => candidate.Id == productId);
        if (product is not null)
        {
            var movements = await context.StockMovements.Where(movement => movement.ProductId == product.Id).ToListAsync();
            context.StockMovements.RemoveRange(movements);
            context.Products.Remove(product);
        }

        if (supplier is not null)
        {
            context.Suppliers.Remove(supplier);
        }

        await context.SaveChangesAsync();
    }

    private sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "supplier-invoice-test-user"),
                new Claim(ClaimTypes.Role, role)
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
