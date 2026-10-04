using CommandCenter.Tests.Builders;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SupplierInvoiceCreationTests
{
    private const string ValidBarcode = "7591234567890";
    private const string BarcodeErrorMessage = "The product barcode must be a valid EAN/UPC (8 to 14 digits).";
    private const string EmptyNameMessage = "A product name is required.";
    private const string LongNameMessage = "The product name cannot exceed 100 characters.";

    [Fact]
    public async Task CreateProductFromLineAsync_CreatesIdentityOnlyProductResolvesLineAndPersistsAlias()
    {
        var (service, context) = CreateService();
        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.BsS,
            36.51m,
            CreateLine(
                supplierCode: "ABC-123",
                quantity: 2m,
                unitCostUsd: 27.39m,
                marginRetailOverride: 30m,
                marginWholesaleOverride: null));
        var line = Assert.Single(invoice.Lines);

        var result = await service.CreateProductFromLineAsync(
            invoice.Id,
            line.Id,
            new CreateInvoiceProductRequestDto($"  {ValidBarcode}  ", "  Harina P.A.N. 1kg  "));

        var product = await context.Products.AsNoTracking().SingleAsync();
        Assert.Equal(ValidBarcode, product.SKU);
        Assert.Equal("Harina P.A.N. 1kg", product.Name);
        Assert.Equal(0m, product.CostPriceUSD);
        Assert.Equal(0m, product.ProfitMarginRetail);
        Assert.Equal(0m, product.ProfitMarginWholesale);
        Assert.Equal(0m, product.PriceUSD);
        Assert.Equal(0m, product.PriceRetailUSD);
        Assert.Equal(0m, product.PriceWholesaleUSD);
        Assert.Equal(0m, product.StockQuantity);
        Assert.False(product.HasWholesale);

        var savedLine = await context.SupplierInvoiceLines.AsNoTracking().SingleAsync(candidate => candidate.Id == line.Id);
        Assert.Equal(product.Id, savedLine.ResolvedProductId);
        Assert.Equal(SupplierInvoiceLineStatus.Update, savedLine.Status);
        Assert.Equal(0m, savedLine.OldCostPriceUSD);
        Assert.Equal(0m, savedLine.OldProfitMarginRetail);
        Assert.Equal(0m, savedLine.OldProfitMarginWholesale);
        Assert.Equal(0m, savedLine.OldStockQuantity);
        Assert.Equal(27.39m, savedLine.UnitCostDocument);
        Assert.Equal(27.39m, savedLine.UnitCostUSD);
        Assert.Equal(30m, savedLine.MarginRetailOverride);
        Assert.Null(savedLine.MarginWholesaleOverride);
        Assert.Equal(
            PricingCalculator.RoundPriceUp(27.39m * 1.30m),
            savedLine.SuggestedRetailPriceUSD);
        Assert.Equal(
            PricingCalculator.RoundPriceUp(27.39m * 1.30m),
            savedLine.SuggestedWholesalePriceUSD);

        var alias = await context.SupplierProductCodes.AsNoTracking().SingleAsync();
        Assert.Equal(invoice.SupplierId, alias.SupplierId);
        Assert.Equal("ABC-123", alias.Code);
        Assert.Equal(product.Id, alias.ProductId);

        var resultLine = Assert.Single(result.Lines);
        Assert.Equal(product.Id, resultLine.ResolvedProductId);
        Assert.Equal("Update", resultLine.Status);
        Assert.Equal(CurrencyCodes.BsS, result.Currency);
        Assert.Equal(36.51m, result.AppliedRate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCDEFGH")]
    [InlineData("1234567")]
    [InlineData("123456789012345")]
    public async Task CreateProductFromLineAsync_RejectsInvalidBarcodesWithoutSideEffects(string barcode)
    {
        var (service, context) = CreateService();
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, CreateLine());
        var line = Assert.Single(invoice.Lines);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProductFromLineAsync(
            invoice.Id,
            line.Id,
            new CreateInvoiceProductRequestDto(barcode, "Product")));

        Assert.Equal(BarcodeErrorMessage, exception.Message);
        await AssertNoCreationSideEffectsAsync(context, invoice.Id, line.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateProductFromLineAsync_RejectsEmptyNameWithoutSideEffects(string name)
    {
        var (service, context) = CreateService();
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, CreateLine());
        var line = Assert.Single(invoice.Lines);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProductFromLineAsync(
            invoice.Id,
            line.Id,
            new CreateInvoiceProductRequestDto(ValidBarcode, name)));

        Assert.Equal(EmptyNameMessage, exception.Message);
        await AssertNoCreationSideEffectsAsync(context, invoice.Id, line.Id);
    }

    [Fact]
    public async Task CreateProductFromLineAsync_RejectsOverlongNameWithoutSideEffects()
    {
        var (service, context) = CreateService();
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, CreateLine());
        var line = Assert.Single(invoice.Lines);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProductFromLineAsync(
            invoice.Id,
            line.Id,
            new CreateInvoiceProductRequestDto(ValidBarcode, new string('A', 101))));

        Assert.Equal(LongNameMessage, exception.Message);
        await AssertNoCreationSideEffectsAsync(context, invoice.Id, line.Id);
    }

    [Fact]
    public async Task CreateProductFromLineAsync_RejectsAlreadyResolvedLineWithoutSideEffects()
    {
        var (service, context) = CreateService();
        var existingProduct = new Product { Name = "Resolved", SKU = "RESOLVED-SKU" };
        context.Products.Add(existingProduct);
        await context.SaveChangesAsync();
        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            CreateLine(
                status: SupplierInvoiceLineStatus.Update,
                resolvedProductId: existingProduct.Id));
        var line = Assert.Single(invoice.Lines);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateProductFromLineAsync(
            invoice.Id,
            line.Id,
            new CreateInvoiceProductRequestDto(ValidBarcode, "Product")));

        Assert.Equal(
            $"Supplier invoice line {line.Id} already resolves to a product.",
            exception.Message);
        Assert.Single(await context.Products.AsNoTracking().ToListAsync());
        Assert.Empty(await context.SupplierProductCodes.AsNoTracking().ToListAsync());
        var savedLine = await context.SupplierInvoiceLines.AsNoTracking().SingleAsync(candidate => candidate.Id == line.Id);
        Assert.Equal(existingProduct.Id, savedLine.ResolvedProductId);
    }

    [Fact]
    public async Task CreateProductFromLineAsync_RejectsNonDraftInvoiceWithoutSideEffects()
    {
        var (service, context) = CreateService();
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, CreateLine());
        var line = Assert.Single(invoice.Lines);
        invoice.Status = SupplierInvoiceStatus.Applied;
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateProductFromLineAsync(
            invoice.Id,
            line.Id,
            new CreateInvoiceProductRequestDto(ValidBarcode, "Product")));

        Assert.Equal($"Supplier invoice {invoice.Id} is not a draft.", exception.Message);
        await AssertNoCreationSideEffectsAsync(context, invoice.Id, line.Id, SupplierInvoiceStatus.Applied);
    }

    [Fact]
    public async Task CreateProductFromLineAsync_RejectsLineFromAnotherInvoiceWithoutSideEffects()
    {
        var (service, context) = CreateService();
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, CreateLine());
        var line = Assert.Single(invoice.Lines);
        const int foreignLineId = 987654;

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateProductFromLineAsync(
            invoice.Id,
            foreignLineId,
            new CreateInvoiceProductRequestDto(ValidBarcode, "Product")));

        Assert.Equal(
            $"Supplier invoice line {foreignLineId} was not found in invoice {invoice.Id}.",
            exception.Message);
        await AssertNoCreationSideEffectsAsync(context, invoice.Id, line.Id);
    }

    [Fact]
    public async Task CreateProductFromLineAsync_RejectsMissingInvoiceWithoutSideEffects()
    {
        var (service, context) = CreateService();
        const int missingInvoiceId = 123456;

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateProductFromLineAsync(
            missingInvoiceId,
            1,
            new CreateInvoiceProductRequestDto(ValidBarcode, "Product")));

        Assert.Equal($"Supplier invoice {missingInvoiceId} was not found.", exception.Message);
        Assert.Empty(await context.Products.AsNoTracking().ToListAsync());
        Assert.Empty(await context.SupplierProductCodes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateProductFromLineAsync_RejectsNullRequest()
    {
        var (service, context) = CreateService();
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, CreateLine());
        var line = Assert.Single(invoice.Lines);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CreateProductFromLineAsync(
            invoice.Id,
            line.Id,
            null!));
    }

    [Fact]
    public async Task CreateProductFromLineAsync_RejectsDuplicateSkuWithoutAliasOrLineResolution()
    {
        var (service, context) = CreateService();
        context.Products.Add(new Product { Name = "Existing", SKU = ValidBarcode });
        await context.SaveChangesAsync();
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, CreateLine(supplierCode: "DUP-CODE"));
        var line = Assert.Single(invoice.Lines);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateProductFromLineAsync(
            invoice.Id,
            line.Id,
            new CreateInvoiceProductRequestDto(ValidBarcode, "Duplicate product")));

        Assert.Equal($"Product with SKU {ValidBarcode} already exists.", exception.Message);
        Assert.Single(await context.Products.AsNoTracking().ToListAsync());
        Assert.Empty(await context.SupplierProductCodes.AsNoTracking().ToListAsync());
        var savedLine = await context.SupplierInvoiceLines.AsNoTracking().SingleAsync(candidate => candidate.Id == line.Id);
        Assert.Null(savedLine.ResolvedProductId);
        Assert.Equal(SupplierInvoiceLineStatus.New, savedLine.Status);
    }

    [Fact]
    public async Task CreateProductFromLineAsync_RejectsCashierWithoutSideEffects()
    {
        var (service, context) = CreateService(canMutateCatalog: false);
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, CreateLine());
        var line = Assert.Single(invoice.Lines);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateProductFromLineAsync(
            invoice.Id,
            line.Id,
            new CreateInvoiceProductRequestDto(ValidBarcode, "Product")));

        Assert.Contains("permisos", exception.Message);
        await AssertNoCreationSideEffectsAsync(context, invoice.Id, line.Id);
    }

    private static (SupplierInvoiceService Service, InventoryDbContext Context) CreateService(
        bool canMutateCatalog = true)
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var settings = Mock.Of<ISystemSettingsService>();
        var similaritySearch = Mock.Of<ISupplierProductSimilaritySearch>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(user => user.CanMutateCatalog).Returns(canMutateCatalog);
        currentUser.SetupGet(user => user.UserId).Returns("creation-test-user");
        var productManagementService = new InventoryService(context, currentUser.Object, null);
        return (
            new SupplierInvoiceService(
                context,
                settings,
                similaritySearch,
                currentUser.Object,
                productManagementService),
            context);
    }

    private static async Task<SupplierInvoice> AddInvoiceAsync(
        InventoryDbContext context,
        string currency,
        decimal appliedRate,
        params SupplierInvoiceLine[] lines)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var supplier = new Supplier
        {
            CommercialName = $"Creation Test Supplier {suffix}",
            NormalizedCommercialName = $"CREATION TEST SUPPLIER {suffix}"
        };
        var invoice = new SupplierInvoice
        {
            Supplier = supplier,
            Status = SupplierInvoiceStatus.Draft,
            Currency = currency,
            AppliedRate = appliedRate,
            Lines = new List<SupplierInvoiceLine>(lines)
        };

        context.SupplierInvoices.Add(invoice);
        await context.SaveChangesAsync();
        return invoice;
    }

    private static SupplierInvoiceLine CreateLine(
        string? supplierCode = "ABC-123",
        string? name = "Invoice row",
        decimal quantity = 1m,
        decimal unitCostUsd = 10m,
        SupplierInvoiceLineStatus status = SupplierInvoiceLineStatus.New,
        int? resolvedProductId = null,
        decimal? marginRetailOverride = null,
        decimal? marginWholesaleOverride = null) => new()
    {
        SupplierCode = supplierCode,
        Barcode = "INVOICE-ROW-1",
        Name = name,
        Quantity = quantity,
        UnitCostDocument = unitCostUsd,
        UnitCostUSD = unitCostUsd,
        Status = status,
        ResolvedProductId = resolvedProductId,
        MarginRetailOverride = marginRetailOverride,
        MarginWholesaleOverride = marginWholesaleOverride,
        MatchMethod = MatchMethod.None
    };

    private static async Task AssertNoCreationSideEffectsAsync(
        InventoryDbContext context,
        int invoiceId,
        int lineId,
        SupplierInvoiceStatus expectedStatus = SupplierInvoiceStatus.Draft)
    {
        Assert.Empty(await context.Products.AsNoTracking().ToListAsync());
        Assert.Empty(await context.SupplierProductCodes.AsNoTracking().ToListAsync());
        var savedLine = await context.SupplierInvoiceLines.AsNoTracking().SingleAsync(candidate => candidate.Id == lineId);
        Assert.Null(savedLine.ResolvedProductId);
        var savedInvoice = await context.SupplierInvoices.AsNoTracking().SingleAsync(candidate => candidate.Id == invoiceId);
        Assert.Equal(expectedStatus, savedInvoice.Status);
    }
}
