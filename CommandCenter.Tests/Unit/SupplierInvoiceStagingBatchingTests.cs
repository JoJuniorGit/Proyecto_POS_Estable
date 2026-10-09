using System.Data.Common;
using System.Globalization;
using CommandCenter.Tests.Builders;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.155/PEF-01 (REQ-SIB-01/03): evidencia de batching del staging de facturas de proveedor.
/// Los lookups exactos (barcode/supplierCode) se precargan UNA vez por stage y las lecturas de
/// producto no escalan con la cantidad de líneas; el fuzzy queda reservado a líneas sin match
/// exacto con nombre.
/// </summary>
public class SupplierInvoiceStagingBatchingTests
{
    private const double Threshold = 0.30d;

    private readonly ITestOutputHelper _output;

    public SupplierInvoiceStagingBatchingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task StageAsync_ManyExactLines_ReadsAreBoundedAndDoNotScaleWithLineCount()
    {
        var twoLineReads = await CountStageReaderCommandsAsync(barcodeLineCount: 1, supplierCodeLineCount: 1);
        var nineLineReads = await CountStageReaderCommandsAsync(barcodeLineCount: 3, supplierCodeLineCount: 6);

        _output.WriteLine($"Lecturas SELECT - 2 líneas: {twoLineReads}; 9 líneas: {nineLineReads}.");

        Assert.True(
            nineLineReads <= 12,
            $"Se esperaban lecturas acotadas para 9 líneas resueltas, se observaron {nineLineReads}.");
        Assert.True(
            Math.Abs(nineLineReads - twoLineReads) <= 1,
            $"Las lecturas escalaron con las líneas: 2 líneas => {twoLineReads}, 9 líneas => {nineLineReads}.");
    }

    [Fact]
    public async Task StageAsync_FuzzyFallbackRunsOnlyForUnmatchedLinesWithName()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = await CreateSqliteContextAsync(connection);
        var similaritySearch = CreateSimilaritySearch();
        var service = CreateService(context, similaritySearch);

        var supplier = await AddSupplierAsync(context);
        var barcodeProduct = CreateProduct("Pinned barcode product", "BAR-PIN", 10m);
        var supplierCodeProduct = CreateProduct("Pinned code product", "COD-PIN", 11m);
        context.Products.AddRange(barcodeProduct, supplierCodeProduct);
        await context.SaveChangesAsync();
        context.SupplierProductCodes.Add(new SupplierProductCode
        {
            SupplierId = supplier.Id,
            ProductId = supplierCodeProduct.Id,
            Code = "SUP-PIN"
        });
        await context.SaveChangesAsync();

        var invoice = await service.StageAsync(new StageSupplierInvoiceRequestDto(
            supplier.Id,
            null,
            null,
            CreateMapping(),
            new[]
            {
                new StageLineDto(null, "BAR-PIN", "Pinned barcode product", 1m, 10m),
                new StageLineDto("SUP-PIN", null, "Pinned code product", 1m, 11m),
                new StageLineDto(null, null, "No exact match product", 1m, 12m),
                new StageLineDto("UNMATCHED-CODE", null, null, 1m, 13m)
            },
            CurrencyCodes.Usd,
            1m));

        Assert.Collection(
            invoice.Lines,
            line =>
            {
                Assert.Equal(barcodeProduct.Id, line.ResolvedProductId);
                Assert.Equal("Barcode", line.MatchMethod);
            },
            line =>
            {
                Assert.Equal(supplierCodeProduct.Id, line.ResolvedProductId);
                Assert.Equal("SupplierCode", line.MatchMethod);
            },
            line =>
            {
                Assert.Null(line.ResolvedProductId);
                Assert.Equal("None", line.MatchMethod);
            },
            line =>
            {
                Assert.Null(line.ResolvedProductId);
                Assert.Equal("None", line.MatchMethod);
            });

        similaritySearch.Verify(
            search => search.FindCandidatesAsync("No exact match product", Threshold, It.IsAny<CancellationToken>()),
            Times.Once);
        similaritySearch.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StageAsync_DuplicateBarcodeOrSupplierCode_ChoosesLowestProductIdOnEveryRun()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context, CreateSimilaritySearch());
        var supplier = await AddSupplierAsync(context);

        var firstBarcodeProduct = CreateProduct("Duplicate barcode first", "DUP-SKU", 10m);
        var secondBarcodeProduct = CreateProduct("Duplicate barcode second", "DUP-SKU", 11m);
        var firstCodeProduct = CreateProduct("Duplicate code first", "DUP-CODE-A", 12m);
        var secondCodeProduct = CreateProduct("Duplicate code second", "DUP-CODE-B", 13m);
        context.Products.AddRange(firstBarcodeProduct, secondBarcodeProduct, firstCodeProduct, secondCodeProduct);
        await context.SaveChangesAsync();
        context.SupplierProductCodes.AddRange(
            new SupplierProductCode { SupplierId = supplier.Id, ProductId = firstCodeProduct.Id, Code = "DUP-CODE" },
            new SupplierProductCode { SupplierId = supplier.Id, ProductId = secondCodeProduct.Id, Code = "DUP-CODE" });
        await context.SaveChangesAsync();

        Assert.True(firstBarcodeProduct.Id < secondBarcodeProduct.Id);
        Assert.True(firstCodeProduct.Id < secondCodeProduct.Id);

        var lines = new[]
        {
            new StageLineDto(null, "DUP-SKU", "Duplicate barcode line", 1m, 10m),
            new StageLineDto("DUP-CODE", null, null, 1m, 12m)
        };

        var firstInvoice = await service.StageAsync(CreateRequest(supplier.Id, lines));
        var secondInvoice = await service.StageAsync(CreateRequest(supplier.Id, lines, includeMapping: false));

        AssertLowestIdChoices(firstInvoice.Lines, firstBarcodeProduct.Id, firstCodeProduct.Id);
        AssertLowestIdChoices(secondInvoice.Lines, firstBarcodeProduct.Id, firstCodeProduct.Id);
    }

    private static async Task<int> CountStageReaderCommandsAsync(int barcodeLineCount, int supplierCodeLineCount)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var interceptor = new CountingCommandInterceptor();
        await using var context = await CreateSqliteContextAsync(connection, interceptor);
        var service = CreateService(context, CreateSimilaritySearch());

        var supplier = await AddSupplierAsync(context);
        var barcodeProducts = Enumerable.Range(0, barcodeLineCount)
            .Select(index => CreateProduct($"Barcode product {index}", $"BAR-{index:000}", 10m + index))
            .ToList();
        var supplierCodeProducts = Enumerable.Range(0, supplierCodeLineCount)
            .Select(index => CreateProduct($"Code product {index}", $"COD-{index:000}", 20m + index))
            .ToList();
        context.Products.AddRange(barcodeProducts);
        context.Products.AddRange(supplierCodeProducts);
        await context.SaveChangesAsync();
        context.SupplierProductCodes.AddRange(supplierCodeProducts.Select((product, index) => new SupplierProductCode
        {
            SupplierId = supplier.Id,
            ProductId = product.Id,
            Code = $"SUP-{index:000}"
        }));
        await context.SaveChangesAsync();

        var lines = barcodeProducts
            .Select((_, index) => new StageLineDto(null, $"BAR-{index:000}", $"Barcode product {index}", 1m, 10m + index))
            .Concat(supplierCodeProducts.Select((_, index) => new StageLineDto($"SUP-{index:000}", null, $"Code product {index}", 1m, 20m + index)))
            .ToArray();

        interceptor.Reset();

        await service.StageAsync(new StageSupplierInvoiceRequestDto(
            supplier.Id,
            null,
            null,
            CreateMapping(),
            lines,
            CurrencyCodes.Usd,
            1m), CancellationToken.None);

        return interceptor.SelectReaderCount;
    }

    private static async Task<InventoryDbContext> CreateSqliteContextAsync(
        SqliteConnection connection,
        CountingCommandInterceptor? interceptor = null)
    {
        var setupOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var setupContext = new InventoryDbContext(setupOptions))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        var builder = new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite(connection);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new InventoryDbContext(builder.Options);
    }

    private static SupplierInvoiceService CreateService(
        InventoryDbContext context,
        Mock<ISupplierProductSimilaritySearch> similaritySearch)
    {
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(service => service.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync(Threshold.ToString(CultureInfo.InvariantCulture));

        return new SupplierInvoiceService(
            context,
            settings.Object,
            similaritySearch.Object,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IProductManagementService>());
    }

    private static Mock<ISupplierProductSimilaritySearch> CreateSimilaritySearch()
    {
        var similaritySearch = new Mock<ISupplierProductSimilaritySearch>();
        similaritySearch.Setup(search => search.FindCandidatesAsync(
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SupplierProductSimilarityCandidate>());
        return similaritySearch;
    }

    private static async Task<Supplier> AddSupplierAsync(InventoryDbContext context)
    {
        var supplier = new Supplier
        {
            RifOrNit = "J-12345678-9",
            NormalizedRifOrNit = "J123456789",
            CommercialName = "Batching Test Supplier",
            NormalizedCommercialName = "BATCHING TEST SUPPLIER"
        };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        return supplier;
    }

    private static Product CreateProduct(string name, string sku, decimal cost) => new()
    {
        Name = name,
        SKU = sku,
        CostPriceUSD = cost,
        ProfitMarginRetail = 20m,
        ProfitMarginWholesale = 12m,
        ProfitPercentage = 20m,
        StockQuantity = 3m
    };

    private static StageSupplierInvoiceRequestDto CreateRequest(
        int supplierId,
        IReadOnlyList<StageLineDto> lines,
        bool includeMapping = true) => new(
        supplierId,
        null,
        null,
        includeMapping ? CreateMapping() : null,
        lines,
        CurrencyCodes.Usd,
        1m);

    private static SupplierColumnMappingDto CreateMapping() => new(
        "Barcode",
        "SupplierCode",
        "Name",
        "Quantity",
        "UnitCost");

    private static void AssertLowestIdChoices(
        IReadOnlyList<SupplierInvoiceLineDto> lines,
        int expectedBarcodeProductId,
        int expectedSupplierCodeProductId)
    {
        Assert.Collection(
            lines,
            line =>
            {
                Assert.Equal(expectedBarcodeProductId, line.ResolvedProductId);
                Assert.Equal("Barcode", line.MatchMethod);
            },
            line =>
            {
                Assert.Equal(expectedSupplierCodeProductId, line.ResolvedProductId);
                Assert.Equal("SupplierCode", line.MatchMethod);
            });
    }

    /// <summary>
    /// Cuenta comandos de lectura que inician con SELECT: los INSERT ... RETURNING del SaveChanges
    /// de SQLite también pasan por ReaderExecuting y no deben contaminar la evidencia de lecturas.
    /// </summary>
    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int _selectReaderCount;

        public int SelectReaderCount => Volatile.Read(ref _selectReaderCount);

        public void Reset() => Interlocked.Exchange(ref _selectReaderCount, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CountIfSelect(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CountIfSelect(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void CountIfSelect(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _selectReaderCount);
            }
        }
    }
}
