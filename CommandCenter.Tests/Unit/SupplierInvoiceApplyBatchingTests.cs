using System.Data.Common;
using CommandCenter.Tests.Builders;
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
/// 8.155/PERF-02 (REQ-AIO-01/02/03): evidencia de batching del apply de facturas de proveedor.
/// La precarga trackeada de productos y de alias deja las lecturas planas frente al N de líneas;
/// el alias se persiste con el guardado final de la factura (last-write-wins) sin romper el
/// savepoint/retry xmin por línea (spec supplier-invoice-apply L64-73).
/// </summary>
public class SupplierInvoiceApplyBatchingTests
{
    private readonly ITestOutputHelper _output;

    public SupplierInvoiceApplyBatchingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ConfirmAsync_ApprovedLines_SelectsAndSavesAreBoundedAndDoNotScaleWithLineCount()
    {
        var twoLines = await RunConfirmIoCountsAsync(lineCount: 2);
        var fourLines = await RunConfirmIoCountsAsync(lineCount: 4);
        var sixLines = await RunConfirmIoCountsAsync(lineCount: 6);

        _output.WriteLine(
            $"SELECTs por líneas -> 2: {twoLines.Selects}; 4: {fourLines.Selects}; 6: {sixLines.Selects}.");
        _output.WriteLine(
            $"SaveChanges por líneas -> 2: {twoLines.Saves}; 4: {fourLines.Saves}; 6: {sixLines.Saves}.");
        _output.WriteLine(
            $"Comandos de escritura por líneas -> 2: {twoLines.Writes}; 4: {fourLines.Writes}; 6: {sixLines.Writes}.");

        Assert.True(
            fourLines.Selects <= 5,
            $"Se esperaban lecturas acotadas para 4 líneas, se observaron {fourLines.Selects}.");
        Assert.True(
            sixLines.Selects <= twoLines.Selects + 1,
            $"Las lecturas escalaron con las líneas: 2 líneas => {twoLines.Selects}, 6 líneas => {sixLines.Selects}.");
        // REQ-AIO-02: saves <= N + 2 (los savepoints por línea se conservan como N saves de línea+movimiento).
        Assert.True(
            fourLines.Saves <= 4 + 2,
            $"Se esperaban <= N+2 SaveChanges para 4 líneas, se observaron {fourLines.Saves}.");
        Assert.True(
            sixLines.Saves <= 6 + 2,
            $"Se esperaban <= N+2 SaveChanges para 6 líneas, se observaron {sixLines.Saves}.");
        Assert.True(
            sixLines.Saves <= twoLines.Saves + 4,
            $"Los SaveChanges escalaron más allá de lo lineal por línea: 2 líneas => {twoLines.Saves}, 6 líneas => {sixLines.Saves}.");
    }

    [Fact]
    public async Task ConfirmAsync_RepeatedSupplierCodeOnTwoLines_LastAppliedLineWins()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = await CreateSqliteContextAsync(connection);
        var service = CreateService(context);

        var firstProduct = CreateProduct("APPLY-BATCH-ALIAS-FIRST", 10m);
        var secondProduct = CreateProduct("APPLY-BATCH-ALIAS-SECOND", 11m);
        context.Products.AddRange(firstProduct, secondProduct);
        await context.SaveChangesAsync();
        Assert.True(firstProduct.Id < secondProduct.Id);

        var invoice = await AddInvoiceAsync(
            context,
            CreateLine(firstProduct, 2m, 5m, "SHARED-CODE"),
            CreateLine(secondProduct, 3m, 6m, "SHARED-CODE"));
        var lines = invoice.Lines.OrderBy(line => line.Id).ToArray();

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(lines[0].Id, true, null, null),
            new ConfirmLineDto(lines[1].Id, true, null, null)
        }));

        var alias = await context.SupplierProductCodes.SingleAsync();
        Assert.Equal("SHARED-CODE", alias.Code);
        // El orden de aplicación es asc por ResolvedProductId: gana la última línea aplicada.
        Assert.Equal(secondProduct.Id, alias.ProductId);
    }

    [Fact]
    public async Task ConfirmAsync_ExistingAliasForAnotherProduct_IsCorrectedByTheConfirmedProduct()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = await CreateSqliteContextAsync(connection);
        var service = CreateService(context);

        var previousProduct = CreateProduct("APPLY-BATCH-ALIAS-OLD", 10m);
        var confirmedProduct = CreateProduct("APPLY-BATCH-ALIAS-NEW", 11m);
        context.Products.AddRange(previousProduct, confirmedProduct);
        await context.SaveChangesAsync();

        var invoice = await AddInvoiceAsync(context, CreateLine(confirmedProduct, 1m, 5m, "CORRECT-ME"));
        var line = Assert.Single(invoice.Lines);
        context.SupplierProductCodes.Add(new SupplierProductCode
        {
            SupplierId = invoice.SupplierId,
            ProductId = previousProduct.Id,
            Code = "CORRECT-ME"
        });
        await context.SaveChangesAsync();

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(line.Id, true, null, null)
        }));

        context.ChangeTracker.Clear();
        var alias = await context.SupplierProductCodes.SingleAsync();
        Assert.Equal("CORRECT-ME", alias.Code);
        Assert.Equal(confirmedProduct.Id, alias.ProductId);
    }

    [Fact]
    public async Task ConfirmAsync_BlankOrMissingSupplierCodes_WriteNoAliasRows()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = await CreateSqliteContextAsync(connection);
        var service = CreateService(context);

        var codedProduct = CreateProduct("APPLY-BATCH-CODED", 10m);
        var blankProduct = CreateProduct("APPLY-BATCH-BLANK", 11m);
        var nullProduct = CreateProduct("APPLY-BATCH-NULL", 12m);
        context.Products.AddRange(codedProduct, blankProduct, nullProduct);
        await context.SaveChangesAsync();

        var invoice = await AddInvoiceAsync(
            context,
            CreateLine(codedProduct, 1m, 5m, "KEEP-1"),
            CreateLine(blankProduct, 1m, 6m, "   "),
            CreateLine(nullProduct, 1m, 7m, null));
        var lines = invoice.Lines.OrderBy(line => line.Id).ToArray();

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(lines[0].Id, true, null, null),
            new ConfirmLineDto(lines[1].Id, true, null, null),
            new ConfirmLineDto(lines[2].Id, true, null, null)
        }));

        var alias = await context.SupplierProductCodes.SingleAsync();
        Assert.Equal("KEEP-1", alias.Code);
        Assert.Equal(codedProduct.Id, alias.ProductId);
    }

    [Fact]
    public async Task ConfirmAsync_LaterInvalidLine_RollsBackAppliedProductAndAliasChanges()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = await CreateSqliteContextAsync(connection);
        var service = CreateService(context);

        var firstProduct = CreateProduct("APPLY-BATCH-ROLLBACK-FIRST", 10m);
        var invalidProduct = CreateProduct("APPLY-BATCH-ROLLBACK-INVALID", 11m);
        context.Products.AddRange(firstProduct, invalidProduct);
        await context.SaveChangesAsync();
        Assert.True(firstProduct.Id < invalidProduct.Id);

        var validLine = CreateLine(firstProduct, 2m, 5m, "ROLLBACK-CODE");
        var invalidLine = CreateLine(invalidProduct, 2m, 5m);
        invalidLine.UnitCostUSD = -1m;
        var invoice = await AddInvoiceAsync(context, validLine, invalidLine);
        var lines = invoice.Lines.OrderBy(line => line.Id).ToArray();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(new[]
            {
                new ConfirmLineDto(lines[0].Id, true, null, null),
                new ConfirmLineDto(lines[1].Id, true, null, null)
            })));

        // ValidateApprovedLine adjunta nameof(line) al ArgumentException (comportamiento intacto).
        Assert.Equal("Approved supplier invoice unit cost cannot be negative. (Parameter 'line')", exception.Message);

        context.ChangeTracker.Clear();
        var savedFirst = await context.Products.SingleAsync(product => product.Id == firstProduct.Id);
        var savedInvoice = await context.SupplierInvoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(10m, savedFirst.CostPriceUSD);
        Assert.Equal(4m, savedFirst.StockQuantity);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
        Assert.Empty(await context.StockMovements.ToListAsync());
        Assert.Empty(await context.SupplierProductCodes.ToListAsync());
    }

    [Fact]
    public async Task ConfirmAsync_ResolvedProductDeleted_ThrowsSameKeyNotFoundExceptionWithoutSideEffects()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = await CreateSqliteContextAsync(connection);
        var service = CreateService(context);

        var product = CreateProduct("APPLY-BATCH-DELETED", 10m);
        var invoice = await AddInvoiceAsync(context, CreateLine(product, 2m, 5m, "DELETED-CODE"));
        var line = Assert.Single(invoice.Lines);

        product.IsDeleted = true;
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(new[]
            {
                new ConfirmLineDto(line.Id, true, null, null)
            })));

        Assert.Equal($"Product {product.Id} was not found or has been deleted.", exception.Message);

        context.ChangeTracker.Clear();
        var savedProduct = await context.Products.SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await context.SupplierInvoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.True(savedProduct.IsDeleted);
        Assert.Equal(10m, savedProduct.CostPriceUSD);
        Assert.Equal(4m, savedProduct.StockQuantity);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
        Assert.Empty(await context.StockMovements.ToListAsync());
        Assert.Empty(await context.SupplierProductCodes.ToListAsync());
    }

    private static async Task<ConfirmIoCounts> RunConfirmIoCountsAsync(int lineCount)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var interceptor = new CountingCommandInterceptor();
        await using var context = await CreateSaveCountingContextAsync(connection, interceptor);
        var service = CreateService(context);

        var supplier = await AddSupplierAsync(context);
        var products = Enumerable.Range(0, lineCount)
            .Select(index => CreateProduct($"Apply batch product {index}", 10m + index))
            .ToList();
        context.Products.AddRange(products);
        await context.SaveChangesAsync();

        var lines = products
            .Select((product, index) => new SupplierInvoiceLine
            {
                SupplierCode = $"BATCH-CODE-{index:000}",
                Name = product.Name,
                Quantity = 1m + index,
                UnitCostDocument = 5m + index,
                UnitCostUSD = 5m + index,
                Status = SupplierInvoiceLineStatus.Update,
                ResolvedProduct = product,
                MatchMethod = MatchMethod.Barcode,
                IsApproved = true
            })
            .ToList();

        var invoice = new SupplierInvoice
        {
            SupplierId = supplier.Id,
            Status = SupplierInvoiceStatus.Draft,
            Lines = lines
        };
        context.SupplierInvoices.Add(invoice);
        await context.SaveChangesAsync();

        var confirmations = lines
            .OrderBy(line => line.Id)
            .Select(line => new ConfirmLineDto(line.Id, true, null, null))
            .ToArray();

        interceptor.Reset();
        context.ResetSaveCount();

        await service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(confirmations),
            CancellationToken.None);

        return new ConfirmIoCounts(interceptor.SelectCount, interceptor.WriteCount, context.SaveCount);
    }

    private static async Task<InventoryDbContext> CreateSqliteContextAsync(
        SqliteConnection connection,
        CountingCommandInterceptor? interceptor = null)
    {
        await EnsureSchemaAsync(connection);
        var builder = new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite(connection);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new InventoryDbContext(builder.Options);
    }

    private static async Task<SaveCountingInventoryDbContext> CreateSaveCountingContextAsync(
        SqliteConnection connection,
        CountingCommandInterceptor interceptor)
    {
        await EnsureSchemaAsync(connection);
        return new SaveCountingInventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options);
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection)
    {
        var setupOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var setupContext = new InventoryDbContext(setupOptions);
        await setupContext.Database.EnsureCreatedAsync();
    }

    private static SupplierInvoiceService CreateService(InventoryDbContext context) => new(
        context,
        Mock.Of<ISystemSettingsService>(),
        Mock.Of<ISupplierProductSimilaritySearch>(),
        Mock.Of<ICurrentUserService>(),
        Mock.Of<IProductManagementService>());

    private static async Task<Supplier> AddSupplierAsync(InventoryDbContext context)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var supplier = new Supplier
        {
            RifOrNit = $"J-{suffix}",
            NormalizedRifOrNit = $"J{suffix}",
            CommercialName = $"Apply Batching Supplier {suffix}",
            NormalizedCommercialName = $"APPLY BATCHING SUPPLIER {suffix}"
        };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        return supplier;
    }

    private static async Task<SupplierInvoice> AddInvoiceAsync(
        InventoryDbContext context,
        params SupplierInvoiceLine[] lines)
    {
        var supplier = await AddSupplierAsync(context);
        var invoice = new SupplierInvoice
        {
            SupplierId = supplier.Id,
            Status = SupplierInvoiceStatus.Draft,
            Lines = new List<SupplierInvoiceLine>(lines)
        };

        context.SupplierInvoices.Add(invoice);
        await context.SaveChangesAsync();
        return invoice;
    }

    private static Product CreateProduct(string name, decimal cost) => new()
    {
        Name = name,
        SKU = $"SKU-{name}",
        CostPriceUSD = cost,
        ProfitMarginRetail = 20m,
        ProfitMarginWholesale = 12m,
        ProfitPercentage = 20m,
        StockQuantity = 4m
    };

    private static SupplierInvoiceLine CreateLine(
        Product product,
        decimal quantity,
        decimal unitCost,
        string? supplierCode = null) => new()
    {
        SupplierCode = supplierCode,
        Name = product.Name,
        Quantity = quantity,
        UnitCostDocument = unitCost,
        UnitCostUSD = unitCost,
        Status = SupplierInvoiceLineStatus.Update,
        ResolvedProduct = product,
        MatchMethod = MatchMethod.Barcode,
        IsApproved = true
    };

    private sealed record ConfirmIoCounts(int Selects, int Writes, int Saves);

    /// <summary>
    /// Cuenta las invocaciones de SaveChangesAsync del confirm (métrica de REQ-AIO-02: saves N+1
    /// vs 2N+1). La consolidación de alias reduce llamadas a SaveChanges; los comandos SQL emitidos
    /// son el mismo conjunto de mutaciones, por eso el conteo de statements no la distingue.
    /// </summary>
    private sealed class SaveCountingInventoryDbContext : InventoryDbContext
    {
        private int _saveCount;

        public SaveCountingInventoryDbContext(DbContextOptions<InventoryDbContext> options)
            : base(options)
        {
        }

        public int SaveCount => Volatile.Read(ref _saveCount);

        public void ResetSaveCount() => Interlocked.Exchange(ref _saveCount, 0);

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCount);
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Cuenta lecturas que inician con SELECT y comandos de escritura (INSERT/UPDATE/DELETE,
    /// incluidos los INSERT ... RETURNING de SQLite que ejecutan por ReaderExecuting). Cada
    /// SaveChanges batcheado de SQLite arriba como un único comando que inicia con su primera
    /// sentencia, por eso el conteo por prefijo no infla la evidencia de escrituras.
    /// </summary>
    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int _selectCount;
        private int _writeCount;

        public int SelectCount => Volatile.Read(ref _selectCount);

        public int WriteCount => Volatile.Read(ref _writeCount);

        public void Reset()
        {
            Interlocked.Exchange(ref _selectCount, 0);
            Interlocked.Exchange(ref _writeCount, 0);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Count(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Count(DbCommand command)
        {
            var text = command.CommandText.TrimStart();
            if (text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _selectCount);
                return;
            }

            if (text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _writeCount);
            }
        }
    }
}
