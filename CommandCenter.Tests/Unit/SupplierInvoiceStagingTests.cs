using System.Globalization;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SupplierInvoiceStagingTests
{
    [Fact]
    public async Task StageAsync_ResolvesSupplierByFiscalIdentity()
    {
        var (service, context) = CreateService();
        var supplier = await AddSupplierAsync(context);

        var invoice = await service.StageAsync(CreateRequest(
            null,
            "j 12345678-9",
            null,
            CreateMapping(),
            new StageLineDto(null, null, "Unmatched product", 1m, 5m)));

        Assert.Equal(supplier.Id, invoice.SupplierId);
        Assert.Equal(1, await context.SupplierInvoices.CountAsync());
    }

    [Fact]
    public async Task StageAsync_UnmatchedSupplierDoesNotPersistDraft()
    {
        var (service, context) = CreateService();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.StageAsync(CreateRequest(
            null,
            "J-99999999-9",
            "Unknown Supplier",
            CreateMapping(),
            new StageLineDto(null, null, "Readable row", 1m, 5m))));

        Assert.Empty(await context.SupplierInvoices.ToListAsync());
        Assert.Empty(await context.SupplierColumnMappings.ToListAsync());
    }

    [Fact]
    public async Task StageAsync_SavesColumnMappingOnFirstImport()
    {
        var (service, context) = CreateService();
        var supplier = await AddSupplierAsync(context);
        var mapping = CreateMapping();

        await service.StageAsync(CreateRequest(
            supplier.Id,
            null,
            null,
            mapping,
            new StageLineDto(null, null, "First product", 2m, 8m)));

        var savedMapping = await context.SupplierColumnMappings.SingleAsync();
        Assert.Equal(supplier.Id, savedMapping.SupplierId);
        Assert.Equal(mapping.NameColumnName, savedMapping.NameColumnName);
        Assert.Equal(mapping.QuantityColumnName, savedMapping.QuantityColumnName);
        Assert.Equal(mapping.UnitCostColumnName, savedMapping.UnitCostColumnName);
        Assert.Equal(mapping.BarcodeColumnName, savedMapping.BarcodeColumnName);
        Assert.Equal(mapping.SupplierCodeColumnName, savedMapping.SupplierCodeColumnName);
    }

    [Fact]
    public async Task StageAsync_ReusesSavedColumnMappingWhenLaterImportOmitsMapping()
    {
        var (service, context) = CreateService();
        var supplier = await AddSupplierAsync(context);
        var savedMapping = CreateMapping();
        await service.StageAsync(CreateRequest(
            supplier.Id,
            null,
            null,
            savedMapping,
            new StageLineDto(null, null, "First product", 1m, 5m)));

        var secondInvoice = await service.StageAsync(CreateRequest(
            supplier.Id,
            null,
            null,
            null,
            new StageLineDto(null, null, "Second product", 1m, 6m)));

        Assert.Equal(supplier.Id, secondInvoice.SupplierId);
        Assert.Equal(2, await context.SupplierInvoices.CountAsync());
        var mappingAfterReuse = await context.SupplierColumnMappings.SingleAsync();
        Assert.Equal(savedMapping.NameColumnName, mappingAfterReuse.NameColumnName);
        Assert.Equal(savedMapping.QuantityColumnName, mappingAfterReuse.QuantityColumnName);
        Assert.Equal(savedMapping.UnitCostColumnName, mappingAfterReuse.UnitCostColumnName);
    }

    [Fact]
    public async Task StageAsync_RejectsEmptyOrUnparseableRowsWithoutPersistingDraft()
    {
        var (service, context) = CreateService();
        var supplier = await AddSupplierAsync(context);
        var mapping = CreateMapping();

        await Assert.ThrowsAsync<ArgumentException>(() => service.StageAsync(CreateRequest(
            supplier.Id,
            null,
            null,
            mapping,
            Array.Empty<StageLineDto>())));
        await Assert.ThrowsAsync<ArgumentException>(() => service.StageAsync(CreateRequest(
            supplier.Id,
            null,
            null,
            mapping,
            new StageLineDto(null, "  ", "   ", 1m, 5m))));

        Assert.Empty(await context.SupplierInvoices.ToListAsync());
    }

    [Fact]
    public async Task StageAsync_ClassifiesNewUpdateUnchangedAndUnmatchedLines()
    {
        var (service, context) = CreateService();
        var supplier = await AddSupplierAsync(context);
        var newProduct = CreateProduct("New product", "SKU-NEW", 0m);
        var updateProduct = CreateProduct("Updated product", "SKU-UPDATE", 10m);
        var unchangedProduct = CreateProduct("Unchanged product", "SKU-SAME", 12m);
        context.Products.AddRange(newProduct, updateProduct, unchangedProduct);
        await context.SaveChangesAsync();

        var invoice = await service.StageAsync(CreateRequest(
            supplier.Id,
            null,
            null,
            CreateMapping(),
            new StageLineDto(null, "SKU-NEW", "New product", 1m, 5m),
            new StageLineDto(null, "SKU-UPDATE", "Updated product", 1m, 11m),
            new StageLineDto(null, "SKU-SAME", "Unchanged product", 1m, 12m),
            new StageLineDto("UNKNOWN-CODE", null, "Unmatched product", 1m, 8m)));

        Assert.Collection(
            invoice.Lines,
            line => Assert.Equal("New", line.Status),
            line => Assert.Equal("Update", line.Status),
            line => Assert.Equal("Unchanged", line.Status),
            line =>
            {
                Assert.Equal("Conflict", line.Status);
                Assert.Null(line.ResolvedProductId);
                Assert.True(line.IsApproved);
            });
    }

    [Fact]
    public async Task StageAsync_DoesNotApplyCostMarginOrStockBeforeConfirm()
    {
        var (service, context) = CreateService();
        var supplier = await AddSupplierAsync(context);
        var product = CreateProduct("Existing product", "SKU-EXISTING", 10m);
        product.PriceUSD = 15m;
        product.PriceRetailUSD = 15m;
        product.PriceWholesaleUSD = 13m;
        product.ProfitMarginRetail = 50m;
        product.ProfitMarginWholesale = 30m;
        product.StockQuantity = 9m;
        context.Products.Add(product);
        await context.SaveChangesAsync();

        var invoice = await service.StageAsync(CreateRequest(
            supplier.Id,
            null,
            null,
            CreateMapping(),
            new StageLineDto(null, "SKU-EXISTING", "Existing product", 4m, 12m)));

        Assert.Equal("Update", Assert.Single(invoice.Lines).Status);
        var unchangedProduct = await context.Products.SingleAsync(candidate => candidate.Id == product.Id);
        Assert.Equal(10m, unchangedProduct.CostPriceUSD);
        Assert.Equal(15m, unchangedProduct.PriceUSD);
        Assert.Equal(15m, unchangedProduct.PriceRetailUSD);
        Assert.Equal(13m, unchangedProduct.PriceWholesaleUSD);
        Assert.Equal(50m, unchangedProduct.ProfitMarginRetail);
        Assert.Equal(30m, unchangedProduct.ProfitMarginWholesale);
        Assert.Equal(9m, unchangedProduct.StockQuantity);
    }

    private static (SupplierInvoiceService Service, InventoryDbContext Context) CreateService()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(service => service.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync(0.30d.ToString(CultureInfo.InvariantCulture));
        var similaritySearch = new Mock<ISupplierProductSimilaritySearch>();
        similaritySearch.Setup(search => search.FindCandidatesAsync(
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SupplierProductSimilarityCandidate>());

        return (new SupplierInvoiceService(context, settings.Object, similaritySearch.Object, Mock.Of<ICurrentUserService>()), context);
    }

    private static async Task<Supplier> AddSupplierAsync(InventoryDbContext context)
    {
        var supplier = new Supplier
        {
            RifOrNit = "J-12345678-9",
            NormalizedRifOrNit = "J123456789",
            CommercialName = "Staging Test Supplier",
            NormalizedCommercialName = "STAGING TEST SUPPLIER"
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
        int? supplierId,
        string? supplierRifOrNit,
        string? supplierCommercialName,
        SupplierColumnMappingDto? mapping,
        params StageLineDto[] lines) => new(
        supplierId,
        supplierRifOrNit,
        supplierCommercialName,
        mapping,
        lines);

    private static SupplierColumnMappingDto CreateMapping() => new(
        "Barcode",
        "SupplierCode",
        "Name",
        "Quantity",
        "UnitCost");
}
