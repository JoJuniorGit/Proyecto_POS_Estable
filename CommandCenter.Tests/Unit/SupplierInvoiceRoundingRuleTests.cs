using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.184-T2 (S5): la regla de redondeo per-línea elegida en la resolución de productos de la
/// factura viaja en LinkInvoiceProductRequestDto y se persiste en la columna nullable
/// (string por nombre, molde StockMovement). null = hereda la regla de la factura; un re-vínculo
/// reemplaza la regla previa (misma semántica que los overrides de margen).
/// </summary>
public class SupplierInvoiceRoundingRuleTests
{
    private static SupplierInvoiceService CreateService(InventoryDbContext context)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(user => user.CanMutateCatalog).Returns(true);
        currentUser.SetupGet(user => user.UserId).Returns("rounding-invoice-tests");
        var productManagementService = new InventoryService(context, currentUser.Object, null);
        return new SupplierInvoiceService(
            context,
            Mock.Of<ISystemSettingsService>(),
            Mock.Of<ISupplierProductSimilaritySearch>(),
            currentUser.Object,
            productManagementService);
    }

    private static Product SeedProduct(InventoryDbContext context)
    {
        var product = new ProductBuilder()
            .WithId(0)
            .WithSku("7599000000011")
            .WithName("Producto factura redondeo")
            .WithCostAndMargin(4.00m, 25m)
            .WithStock(10m)
            .Build();
        context.Products.Add(product);
        return product;
    }

    private static async Task<SupplierInvoice> AddInvoiceAsync(InventoryDbContext context, params SupplierInvoiceLine[] lines)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var invoice = new SupplierInvoice
        {
            Supplier = new Supplier
            {
                CommercialName = $"Rounding Test Supplier {suffix}",
                NormalizedCommercialName = $"ROUNDING TEST SUPPLIER {suffix}"
            },
            Status = SupplierInvoiceStatus.Draft,
            Currency = CurrencyCodes.Usd,
            AppliedRate = 1m,
            Lines = new List<SupplierInvoiceLine>(lines)
        };

        context.SupplierInvoices.Add(invoice);
        await context.SaveChangesAsync();
        return invoice;
    }

    private static SupplierInvoiceLine CreateLine() => new()
    {
        SupplierCode = "RND-ROW-1",
        Barcode = "INVOICE-RND-1",
        Name = "Fila factura redondeo",
        Quantity = 3m,
        UnitCostDocument = 4m,
        UnitCostUSD = 4m,
        Status = SupplierInvoiceLineStatus.New,
        MatchMethod = MatchMethod.None
    };

    private static InventoryDbContext CreateReloadContext(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(connection)
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task LinkProductToLineAsync_WithRoundingRule_PersistsByNameAndSurvivesReload()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (context)
        {
            var service = CreateService(context);
            var product = SeedProduct(context);
            var invoice = await AddInvoiceAsync(context, CreateLine());
            var line = Assert.Single(invoice.Lines);

            await service.LinkProductToLineAsync(
                invoice.Id,
                line.Id,
                new LinkInvoiceProductRequestDto(
                    product.Id,
                    "FILA FACTURA REDONDEO",
                    RoundingRule: RoundingRuleType.UsdPsychological));

            // Recarga desde una instancia nueva sobre la misma conexión: la columna persiste por nombre.
            await using var reloadContext = CreateReloadContext(connection);
            var persisted = await reloadContext.SupplierInvoiceLines.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == line.Id);
            Assert.Equal(RoundingRuleType.UsdPsychological, persisted.RoundingRule);

            var storedRaw = await reloadContext.Database.SqlQueryRaw<string>(
                "SELECT \"RoundingRule\" AS \"Value\" FROM \"SupplierInvoiceLines\" WHERE \"Id\" = {0}",
                line.Id).SingleAsync();
            Assert.Equal(nameof(RoundingRuleType.UsdPsychological), storedRaw);
        }
    }

    [Fact]
    public async Task LinkProductToLineAsync_WithoutRoundingRule_PersistsNullInheritingInvoiceRule()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (context)
        {
            var service = CreateService(context);
            var product = SeedProduct(context);
            var invoice = await AddInvoiceAsync(context, CreateLine());
            var line = Assert.Single(invoice.Lines);

            await service.LinkProductToLineAsync(
                invoice.Id,
                line.Id,
                new LinkInvoiceProductRequestDto(product.Id, "FILA FACTURA REDONDEO"));

            await using var reloadContext = CreateReloadContext(connection);
            var persisted = await reloadContext.SupplierInvoiceLines.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == line.Id);
            Assert.Null(persisted.RoundingRule);
        }
    }

    [Fact]
    public async Task LinkProductToLineAsync_ReLinkWithoutRule_ClearsPreviousPerLineRule()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (context)
        {
            var service = CreateService(context);
            var product = SeedProduct(context);
            var invoice = await AddInvoiceAsync(context, CreateLine());
            var line = Assert.Single(invoice.Lines);

            await service.LinkProductToLineAsync(
                invoice.Id,
                line.Id,
                new LinkInvoiceProductRequestDto(
                    product.Id,
                    "FILA FACTURA REDONDEO",
                    RoundingRule: RoundingRuleType.UsdPsychological));

            // Re-vínculo explícito con "Heredar factura" (null): reemplaza la regla previa.
            await service.LinkProductToLineAsync(
                invoice.Id,
                line.Id,
                new LinkInvoiceProductRequestDto(product.Id, "FILA FACTURA REDONDEO"));

            await using var reloadContext = CreateReloadContext(connection);
            var persisted = await reloadContext.SupplierInvoiceLines.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == line.Id);
            Assert.Null(persisted.RoundingRule);
        }
    }

    [Fact]
    public async Task GetInvoiceAsync_AfterLinkWithRoundingRule_LineDtoCarriesPersistedRule()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (context)
        {
            var service = CreateService(context);
            var product = SeedProduct(context);
            var invoice = await AddInvoiceAsync(context, CreateLine());
            var line = Assert.Single(invoice.Lines);

            await service.LinkProductToLineAsync(
                invoice.Id,
                line.Id,
                new LinkInvoiceProductRequestDto(
                    product.Id,
                    "FILA FACTURA REDONDEO",
                    RoundingRule: RoundingRuleType.UsdPsychological));

            // Recarga del detalle (GetInvoiceAsync = AsNoTracking, consulta fresca): el DTO de línea
            // transporta la regla persistida para que la UI repoble el selector.
            context.ChangeTracker.Clear();
            var reloaded = await service.GetInvoiceAsync(invoice.Id);

            Assert.NotNull(reloaded);
            var reloadedLine = Assert.Single(reloaded!.Lines);
            Assert.Equal(RoundingRuleType.UsdPsychological, reloadedLine.RoundingRule);
        }
    }

    [Fact]
    public async Task GetInvoiceAsync_AfterReLinkWithoutRoundingRule_LineDtoCarriesNullRule()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (context)
        {
            var service = CreateService(context);
            var product = SeedProduct(context);
            var invoice = await AddInvoiceAsync(context, CreateLine());
            var line = Assert.Single(invoice.Lines);

            await service.LinkProductToLineAsync(
                invoice.Id,
                line.Id,
                new LinkInvoiceProductRequestDto(
                    product.Id,
                    "FILA FACTURA REDONDEO",
                    RoundingRule: RoundingRuleType.UsdPsychological));

            // Re-vínculo con "Heredar factura" (null): la recarga del detalle refleja null.
            await service.LinkProductToLineAsync(
                invoice.Id,
                line.Id,
                new LinkInvoiceProductRequestDto(product.Id, "FILA FACTURA REDONDEO"));

            context.ChangeTracker.Clear();
            var reloaded = await service.GetInvoiceAsync(invoice.Id);

            Assert.NotNull(reloaded);
            var reloadedLine = Assert.Single(reloaded!.Lines);
            Assert.Null(reloadedLine.RoundingRule);
        }
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task LinkProductToLineAsync_UndefinedRoundingRule_RejectsWithoutSideEffects(int rawValue)
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (context)
        {
            var service = CreateService(context);
            var product = SeedProduct(context);
            var invoice = await AddInvoiceAsync(context, CreateLine());
            var line = Assert.Single(invoice.Lines);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.LinkProductToLineAsync(
                invoice.Id,
                line.Id,
                new LinkInvoiceProductRequestDto(
                    product.Id,
                    "FILA FACTURA REDONDEO",
                    RoundingRule: (RoundingRuleType)rawValue)));

            Assert.Equal("request", exception.ParamName);
            var persisted = await context.SupplierInvoiceLines.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == line.Id);
            Assert.Null(persisted.RoundingRule);
            Assert.Null(persisted.ResolvedProductId);
        }
    }
}
