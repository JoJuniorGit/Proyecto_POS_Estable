using CommandCenter.Tests.Builders;
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

public class SupplierInvoiceApplyTests
{
    [Fact]
    public async Task ConfirmAsync_AppliesOnlyApprovedResolvedLines()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var approvedProduct = CreateProduct("APPLY-APPROVED", 4m, 5m, 20m);
        var rejectedProduct = CreateProduct("APPLY-REJECTED", 7m, 8m, 15m);
        var invoice = await AddInvoiceAsync(
            context,
            CreateLine(approvedProduct, 2m, 9m),
            CreateLine(rejectedProduct, 3m, 12m),
            CreateLine(null, 4m, 15m, SupplierInvoiceLineStatus.Conflict));
        var lines = invoice.Lines.OrderBy(line => line.Id).ToArray();

        var result = await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(lines[0].Id, true, null, null),
            new ConfirmLineDto(lines[1].Id, false, null, null),
            new ConfirmLineDto(lines[2].Id, true, null, null)
        }));

        var savedApproved = await context.Products.SingleAsync(product => product.Id == approvedProduct.Id);
        var savedRejected = await context.Products.SingleAsync(product => product.Id == rejectedProduct.Id);
        Assert.Equal(nameof(SupplierInvoiceStatus.Applied), result.Status);
        Assert.Equal(9m, savedApproved.CostPriceUSD);
        Assert.Equal(7m, savedApproved.StockQuantity);
        Assert.Equal(7m, savedRejected.CostPriceUSD);
        Assert.Equal(8m, savedRejected.StockQuantity);
        Assert.Equal(1, await context.StockMovements.CountAsync());
    }

    [Fact]
    public async Task ConfirmAsync_UsesRetailMarginUnlessIndependentWholesaleIsEnabled()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var retailOnlyProduct = CreateProduct("APPLY-RETAIL", 2m, 1m, 10m);
        retailOnlyProduct.ProfitMarginWholesale = 5m;
        var independentWholesaleProduct = CreateProduct("APPLY-WHOLESALE", 2m, 1m, 12m);
        independentWholesaleProduct.HasWholesale = true;
        independentWholesaleProduct.ProfitMarginWholesale = 45m;
        var invoice = await AddInvoiceAsync(
            context,
            CreateLine(retailOnlyProduct, 1m, 4m),
            CreateLine(independentWholesaleProduct, 1m, 3m));
        var lines = invoice.Lines.OrderBy(line => line.Id).ToArray();

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(lines[0].Id, true, 33m, null),
            new ConfirmLineDto(lines[1].Id, true, 20m, null)
        }));

        var savedRetailOnly = await context.Products.SingleAsync(product => product.Id == retailOnlyProduct.Id);
        var savedWholesale = await context.Products.SingleAsync(product => product.Id == independentWholesaleProduct.Id);
        Assert.Equal(33m, savedRetailOnly.ProfitMarginRetail);
        Assert.Equal(33m, savedRetailOnly.ProfitMarginWholesale);
        Assert.Equal(savedRetailOnly.PriceRetailUSD, savedRetailOnly.PriceWholesaleUSD);
        Assert.Equal(20m, savedWholesale.ProfitMarginRetail);
        Assert.Equal(45m, savedWholesale.ProfitMarginWholesale);
    }

    [Fact]
    public async Task ConfirmAsync_RecordsMarginOverridesAndUsesRoundPriceUp()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var product = CreateProduct("APPLY-ROUND", 2m, 3m, 10m);
        product.HasWholesale = true;
        product.ProfitMarginWholesale = 5m;
        var invoice = await AddInvoiceAsync(context, CreateLine(product, 1.5m, 1.01m));
        var line = Assert.Single(invoice.Lines);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(line.Id, true, 20.1m, 10.2m)
        }));

        var savedProduct = await context.Products.SingleAsync(candidate => candidate.Id == product.Id);
        var savedLine = await context.SupplierInvoiceLines.SingleAsync(candidate => candidate.Id == line.Id);
        Assert.Equal(20.1m, savedLine.MarginRetailOverride);
        Assert.Equal(10.2m, savedLine.MarginWholesaleOverride);
        Assert.Equal(20.1m, savedProduct.ProfitMarginRetail);
        Assert.Equal(10.2m, savedProduct.ProfitMarginWholesale);
        Assert.Equal(PricingCalculator.RoundPriceUp(1.01m * (1m + 20.1m / 100m)), savedProduct.PriceRetailUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(1.01m * (1m + 10.2m / 100m)), savedProduct.PriceWholesaleUSD);
        Assert.Equal(savedProduct.PriceRetailUSD, savedProduct.PriceUSD);
    }

    [Fact]
    public async Task ConfirmAsync_IncrementsStockAndWritesMovementForActingUser()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var user = new Mock<ICurrentUserService>();
        user.Setup(currentUser => currentUser.UserId).Returns("supplier-invoice-user");
        var service = CreateService(context, user.Object);
        var product = CreateProduct("APPLY-STOCK", 3m, 4m, 25m);
        product.Cost = 91m;
        product.PriceBsS = 123.45m;
        product.LastConversionRate = 36.5m;
        var invoice = await AddInvoiceAsync(context, CreateLine(product, 2.5m, 3.5m));
        var line = Assert.Single(invoice.Lines);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(line.Id, true, null, null)
        }));

        var savedProduct = await context.Products.SingleAsync(candidate => candidate.Id == product.Id);
        var movement = await context.StockMovements.SingleAsync();
        Assert.Equal(6.5m, savedProduct.StockQuantity);
        Assert.Equal(91m, savedProduct.Cost);
        Assert.Equal(123.45m, savedProduct.PriceBsS);
        Assert.Equal(36.5m, savedProduct.LastConversionRate);
        Assert.Equal(product.Id, movement.ProductId);
        Assert.Equal(2.5m, movement.QuantityChange);
        Assert.Equal(6.5m, movement.NewStockLevel);
        Assert.Equal($"Supplier invoice {invoice.Id} applied", movement.Reason);
        Assert.Equal("supplier-invoice-user", movement.UserId);
    }

    [Fact]
    public async Task ConfirmAsync_UpsertsSupplierCodeAliasForApprovedLine()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var product = CreateProduct("APPLY-ALIAS", 4m, 5m, 20m);
        var invoice = await AddInvoiceAsync(context, CreateLine(product, 2m, 9m, supplierCode: "XYZ-9"));
        var line = Assert.Single(invoice.Lines);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(line.Id, true, null, null)
        }));

        var alias = await context.SupplierProductCodes.SingleAsync();
        Assert.Equal(invoice.SupplierId, alias.SupplierId);
        Assert.Equal("XYZ-9", alias.Code);
        Assert.Equal(product.Id, alias.ProductId);
    }

    [Fact]
    public async Task ConfirmAsync_UpdatesExistingAliasToConfirmedProduct()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var confirmedProduct = CreateProduct("APPLY-ALIAS-NEW", 4m, 5m, 20m);
        var previousProduct = CreateProduct("APPLY-ALIAS-OLD", 3m, 4m, 15m);
        context.Products.AddRange(confirmedProduct, previousProduct);
        await context.SaveChangesAsync();
        var invoice = await AddInvoiceAsync(
            context,
            CreateLine(confirmedProduct, 2m, 9m, supplierCode: "XYZ-8"));
        var line = Assert.Single(invoice.Lines);
        context.SupplierProductCodes.Add(new SupplierProductCode
        {
            SupplierId = invoice.SupplierId,
            ProductId = previousProduct.Id,
            Code = "XYZ-8"
        });
        await context.SaveChangesAsync();

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(line.Id, true, null, null)
        }));

        var alias = await context.SupplierProductCodes.SingleAsync();
        Assert.Equal(confirmedProduct.Id, alias.ProductId);
    }

    [Fact]
    public async Task ConfirmAsync_DoesNotWriteAliasWhenSupplierCodeIsMissing()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var product = CreateProduct("APPLY-NO-ALIAS", 4m, 5m, 20m);
        var invoice = await AddInvoiceAsync(context, CreateLine(product, 2m, 9m, supplierCode: null));
        var line = Assert.Single(invoice.Lines);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(line.Id, true, null, null)
        }));

        Assert.Empty(await context.SupplierProductCodes.ToListAsync());
    }

    private static SupplierInvoiceService CreateService(
        InventoryDbContext context,
        ICurrentUserService? currentUser = null)
    {
        var settings = Mock.Of<ISystemSettingsService>();
        var similaritySearch = Mock.Of<ISupplierProductSimilaritySearch>();
        currentUser ??= Mock.Of<ICurrentUserService>();
        return new SupplierInvoiceService(
            context,
            settings,
            similaritySearch,
            currentUser,
            Mock.Of<IProductManagementService>());
    }

    private static async Task<SupplierInvoice> AddInvoiceAsync(
        InventoryDbContext context,
        params SupplierInvoiceLine[] lines)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var supplier = new Supplier
        {
            CommercialName = $"Apply Test Supplier {suffix}",
            NormalizedCommercialName = $"APPLY TEST SUPPLIER {suffix}"
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
        Product? product,
        decimal quantity,
        decimal unitCost,
        SupplierInvoiceLineStatus status = SupplierInvoiceLineStatus.Update,
        string? supplierCode = null) => new()
    {
        SupplierCode = supplierCode,
        Quantity = quantity,
        UnitCostUSD = unitCost,
        Status = status,
        ResolvedProduct = product,
        MatchMethod = product is null ? MatchMethod.None : MatchMethod.Barcode
    };
}
