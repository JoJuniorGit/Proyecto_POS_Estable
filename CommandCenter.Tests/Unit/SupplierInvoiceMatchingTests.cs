using System.Globalization;
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

public class SupplierInvoiceMatchingTests
{
    private const double DefaultThreshold = 0.30d;

    [Fact]
    public async Task StageAsync_BarcodeMatchWinsOverSupplierCodeAndFuzzyName()
    {
        var (service, context, similaritySearch) = CreateService();
        var supplier = await AddSupplierAsync(context);
        var barcodeProduct = CreateProduct("Unrelated product", "BAR-001", 10m);
        var supplierCodeProduct = CreateProduct("Supplier code product", "SKU-002", 12m);
        var fuzzyProduct = CreateProduct("Similar product", "SKU-003", 14m);
        context.Products.AddRange(barcodeProduct, supplierCodeProduct, fuzzyProduct);
        await context.SaveChangesAsync();
        context.SupplierProductCodes.Add(new SupplierProductCode
        {
            SupplierId = supplier.Id,
            ProductId = supplierCodeProduct.Id,
            Code = "SUP-002"
        });
        await context.SaveChangesAsync();
        similaritySearch
            .Setup(search => search.FindCandidatesAsync(
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new SupplierProductSimilarityCandidate(fuzzyProduct.Id, 0.99d) });

        var result = await StageLineAsync(
            service,
            supplier.Id,
            new StageLineDto("SUP-002", "BAR-001", "Similar product", 1m, 11m));

        Assert.Equal(barcodeProduct.Id, result.ResolvedProductId);
        Assert.Equal("Barcode", result.MatchMethod);
        similaritySearch.Verify(search => search.FindCandidatesAsync(
            It.IsAny<string>(),
            It.IsAny<double>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StageAsync_SupplierCodeMatchesWhenBarcodeDoesNotMatch()
    {
        var (service, context, similaritySearch) = CreateService();
        var supplier = await AddSupplierAsync(context);
        var supplierCodeProduct = CreateProduct("Code product", "SKU-010", 10m);
        var fuzzyProduct = CreateProduct("Fuzzy product", "SKU-011", 12m);
        context.Products.AddRange(supplierCodeProduct, fuzzyProduct);
        await context.SaveChangesAsync();
        context.SupplierProductCodes.Add(new SupplierProductCode
        {
            SupplierId = supplier.Id,
            ProductId = supplierCodeProduct.Id,
            Code = "SUP-010"
        });
        await context.SaveChangesAsync();
        similaritySearch
            .Setup(search => search.FindCandidatesAsync(
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new SupplierProductSimilarityCandidate(fuzzyProduct.Id, 0.99d) });

        var result = await StageLineAsync(
            service,
            supplier.Id,
            new StageLineDto("SUP-010", "UNKNOWN-BARCODE", "Fuzzy product", 1m, 11m));

        Assert.Equal(supplierCodeProduct.Id, result.ResolvedProductId);
        Assert.Equal("SupplierCode", result.MatchMethod);
        similaritySearch.Verify(search => search.FindCandidatesAsync(
            It.IsAny<string>(),
            It.IsAny<double>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StageAsync_FuzzyNameFallbackSelectsHighestSimilarity()
    {
        var (service, context, similaritySearch) = CreateService();
        var supplier = await AddSupplierAsync(context);
        var lowerSimilarityProduct = CreateProduct("Lower match", "SKU-020", 10m);
        var higherSimilarityProduct = CreateProduct("Higher match", "SKU-021", 12m);
        context.Products.AddRange(lowerSimilarityProduct, higherSimilarityProduct);
        await context.SaveChangesAsync();
        similaritySearch
            .Setup(search => search.FindCandidatesAsync(
                "Organic milk",
                DefaultThreshold,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SupplierProductSimilarityCandidate(lowerSimilarityProduct.Id, 0.55d),
                new SupplierProductSimilarityCandidate(higherSimilarityProduct.Id, 0.82d)
            });

        var result = await StageLineAsync(
            service,
            supplier.Id,
            new StageLineDto(null, null, "Organic milk", 1m, 13m));

        Assert.Equal(higherSimilarityProduct.Id, result.ResolvedProductId);
        Assert.Equal("Fuzzy", result.MatchMethod);
    }

    [Fact]
    public async Task StageAsync_SimilarityBelowThresholdRemainsUnmatched()
    {
        const double threshold = 0.75d;
        var (service, context, similaritySearch) = CreateService(threshold);
        var supplier = await AddSupplierAsync(context);
        var candidate = CreateProduct("Near match", "SKU-030", 10m);
        context.Products.Add(candidate);
        await context.SaveChangesAsync();
        similaritySearch
            .Setup(search => search.FindCandidatesAsync(
                "Near match",
                threshold,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new SupplierProductSimilarityCandidate(candidate.Id, 0.7499d) });

        var result = await StageLineAsync(
            service,
            supplier.Id,
            new StageLineDto(null, null, "Near match", 1m, 11m));

        Assert.Null(result.ResolvedProductId);
        Assert.Equal("New", result.Status);
        Assert.Equal("None", result.MatchMethod);
    }

    [Fact]
    public async Task StageAsync_SimilarityAtThresholdIsEligibleForMatch()
    {
        const double threshold = 0.75d;
        var (service, context, similaritySearch) = CreateService(threshold);
        var supplier = await AddSupplierAsync(context);
        var candidate = CreateProduct("Boundary match", "SKU-031", 10m);
        context.Products.Add(candidate);
        await context.SaveChangesAsync();
        similaritySearch
            .Setup(search => search.FindCandidatesAsync(
                "Boundary match",
                threshold,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new SupplierProductSimilarityCandidate(candidate.Id, threshold) });

        var result = await StageLineAsync(
            service,
            supplier.Id,
            new StageLineDto(null, null, "Boundary match", 1m, 11m));

        Assert.Equal(candidate.Id, result.ResolvedProductId);
        Assert.Equal("Fuzzy", result.MatchMethod);
    }

    [Fact]
    public async Task StageAsync_EqualSimilarityUsesLowestProductIdDeterministically()
    {
        var (service, context, similaritySearch) = CreateService();
        var supplier = await AddSupplierAsync(context);
        var lowerIdProduct = CreateProduct("First inserted", "SKU-040", 10m);
        var higherIdProduct = CreateProduct("Second inserted", "SKU-041", 12m);
        context.Products.AddRange(lowerIdProduct, higherIdProduct);
        await context.SaveChangesAsync();
        Assert.True(lowerIdProduct.Id < higherIdProduct.Id);
        similaritySearch
            .Setup(search => search.FindCandidatesAsync(
                "Equal candidates",
                DefaultThreshold,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SupplierProductSimilarityCandidate(higherIdProduct.Id, 0.80d),
                new SupplierProductSimilarityCandidate(lowerIdProduct.Id, 0.80d)
            });

        var firstResult = await StageLineAsync(
            service,
            supplier.Id,
            new StageLineDto(null, null, "Equal candidates", 1m, 13m));
        var secondResult = await StageLineAsync(
            service,
            supplier.Id,
            new StageLineDto(null, null, "Equal candidates", 1m, 13m),
            includeMapping: false);

        Assert.Equal(lowerIdProduct.Id, firstResult.ResolvedProductId);
        Assert.Equal(lowerIdProduct.Id, secondResult.ResolvedProductId);
    }

    [Fact]
    public async Task StageAsync_UnmatchedLineIsCreationCandidateAndDoesNotCreateProduct()
    {
        var (service, context, _) = CreateService();
        var supplier = await AddSupplierAsync(context);

        var result = await StageLineAsync(
            service,
            supplier.Id,
            new StageLineDto("UNKNOWN-CODE", null, "New catalog item", 1m, 7m));

        Assert.Null(result.ResolvedProductId);
        Assert.Equal("New", result.Status);
        Assert.Empty(await context.Products.ToListAsync());
    }

    private static (SupplierInvoiceService Service, InventoryDbContext Context, Mock<ISupplierProductSimilaritySearch> SimilaritySearch)
        CreateService(double threshold = DefaultThreshold)
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(service => service.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync(threshold.ToString(CultureInfo.InvariantCulture));
        var similaritySearch = new Mock<ISupplierProductSimilaritySearch>();
        similaritySearch.Setup(search => search.FindCandidatesAsync(
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SupplierProductSimilarityCandidate>());

        return (new SupplierInvoiceService(context, settings.Object, similaritySearch.Object, Mock.Of<ICurrentUserService>()), context, similaritySearch);
    }

    private static async Task<Supplier> AddSupplierAsync(InventoryDbContext context)
    {
        var supplier = new Supplier
        {
            RifOrNit = "J-12345678-9",
            NormalizedRifOrNit = "J123456789",
            CommercialName = "Matching Test Supplier",
            NormalizedCommercialName = "MATCHING TEST SUPPLIER"
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
        ProfitMarginWholesale = 10m,
        ProfitPercentage = 20m,
        StockQuantity = 5m
    };

    private static Task<SupplierInvoiceLineDto> StageLineAsync(
        SupplierInvoiceService service,
        int supplierId,
        StageLineDto line,
        bool includeMapping = true)
    {
        var request = new StageSupplierInvoiceRequestDto(
            supplierId,
            null,
            null,
            includeMapping ? CreateMapping() : null,
            new[] { line },
            CurrencyCodes.Usd,
            1m);

        return StageAsync(service, request);
    }

    private static async Task<SupplierInvoiceLineDto> StageAsync(
        SupplierInvoiceService service,
        StageSupplierInvoiceRequestDto request)
    {
        var invoice = await service.StageAsync(request);
        return Assert.Single(invoice.Lines);
    }

    private static SupplierColumnMappingDto CreateMapping() => new(
        "Barcode",
        "SupplierCode",
        "Name",
        "Quantity",
        "UnitCost");
}
