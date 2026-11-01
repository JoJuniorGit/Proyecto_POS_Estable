using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>
/// 8.211-T3d (F3d): supplier invoices follow the active monetary profile. Single accepts only its
/// primary (identity rate); Dual accepts the primary/USD or the normalized secondary; the legacy
/// composition without a configured profile keeps USD/Bs.S and never reinterprets stored costs.
/// </summary>
public class DualSecondaryCurrencySupplierInvoiceTests
{
    private const decimal CopRate = 4000m;
    private const decimal CopQuote = 4000.12345678m;

    [Fact]
    public async Task StageAsync_SingleCop_UsesIdentityRateAndKeepsPrimaryCost()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = await AddSupplierAsync(context, invoiceCurrency: "USD");
        await AddProductAsync(context, "SKU-SINGLE-COP", costUsd: 1000m);
        var service = CreateService(context, profile: CreateSingleProfileMock("COP").Object);

        var invoice = await service.StageAsync(CreateStageRequest(
            supplier.Id,
            "COP",
            4000m,
            new StageLineDto(null, "SKU-SINGLE-COP", "Primary product", 1m, 1000m)));

        var line = Assert.Single(invoice.Lines);
        Assert.Equal("COP", invoice.Currency);
        Assert.Equal(1m, invoice.AppliedRate);
        Assert.Equal(1000m, line.UnitCostDocument);
        Assert.Equal(1000m, line.UnitCostUSD);

        var persisted = await context.SupplierInvoices.AsNoTracking().SingleAsync();
        Assert.Equal("COP", persisted.Currency);
        Assert.Equal(1m, persisted.AppliedRate);
    }

    [Fact]
    public async Task StageAsync_SingleCop_RejectsUsdAndBsSRequests()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = await AddSupplierAsync(context);
        var service = CreateService(context, profile: CreateSingleProfileMock("COP").Object);

        var usdException = await Assert.ThrowsAsync<ArgumentException>(() => service.StageAsync(
            CreateStageRequest(
                supplier.Id,
                "USD",
                1m,
                new StageLineDto(null, null, "Readable row", 1m, 10m))));
        Assert.Contains("must be COP", usdException.Message, StringComparison.Ordinal);

        var bsSException = await Assert.ThrowsAsync<ArgumentException>(() => service.StageAsync(
            CreateStageRequest(
                supplier.Id,
                CurrencyCodes.BsS,
                50m,
                new StageLineDto(null, null, "Readable row", 1m, 10m))));
        Assert.Contains("must be COP", bsSException.Message, StringComparison.Ordinal);

        Assert.Empty(await context.SupplierInvoices.ToListAsync());
    }

    [Fact]
    public async Task StageAsync_DualCop_SnapshotsFourDecimalsAndConvertsLineCost()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = await AddSupplierAsync(context);
        var service = CreateService(context, profile: CreateDualProfileMock("COP").Object);

        var invoice = await service.StageAsync(CreateStageRequest(
            supplier.Id,
            "COP",
            CopQuote,
            new StageLineDto(null, null, "Secondary product", 1m, 1000m)));

        var line = Assert.Single(invoice.Lines);
        Assert.Equal("COP", invoice.Currency);
        Assert.Equal(4000.1235m, invoice.AppliedRate);
        Assert.Equal(1000m, line.UnitCostDocument);
        Assert.Equal(0.25m, line.UnitCostUSD);

        var persisted = await context.SupplierInvoices.AsNoTracking().SingleAsync();
        Assert.Equal(4000.1235m, persisted.AppliedRate);
    }

    [Fact]
    public async Task StageAsync_DualCop_UsdUsesIdentityRateAndRejectsOtherCurrencies()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = await AddSupplierAsync(context);
        var service = CreateService(context, profile: CreateDualProfileMock("COP").Object);

        var usdInvoice = await service.StageAsync(CreateStageRequest(
            supplier.Id,
            CurrencyCodes.Usd,
            4000m,
            new StageLineDto(null, null, "Usd product", 1m, 1000m)));

        var usdLine = Assert.Single(usdInvoice.Lines);
        Assert.Equal(CurrencyCodes.Usd, usdInvoice.Currency);
        Assert.Equal(1m, usdInvoice.AppliedRate);
        Assert.Equal(1000m, usdLine.UnitCostUSD);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.StageAsync(
            CreateStageRequest(
                supplier.Id,
                CurrencyCodes.BsS,
                50m,
                new StageLineDto(null, null, "Rejected product", 1m, 1000m))));
        Assert.Contains("must be USD or COP", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageAsync_DualVes_NormalizesAliasesAndConverts()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = await AddSupplierAsync(context);
        var service = CreateService(context, profile: CreateDualProfileMock("VES").Object);

        var aliasInvoice = await service.StageAsync(CreateStageRequest(
            supplier.Id,
            "ves",
            50m,
            new StageLineDto(null, null, "Local alias product", 1m, 1000m)));
        var canonicalInvoice = await service.StageAsync(CreateStageRequest(
            supplier.Id,
            CurrencyCodes.BsS,
            50m,
            new StageLineDto(null, null, "Local product", 1m, 1000m)));

        foreach (var invoice in new[] { aliasInvoice, canonicalInvoice })
        {
            Assert.Equal(CurrencyCodes.BsS, invoice.Currency);
            Assert.Equal(50m, invoice.AppliedRate);
            Assert.Equal(20m, Assert.Single(invoice.Lines).UnitCostUSD);
        }
    }

    [Fact]
    public async Task StageAsync_NoProfile_KeepsLegacyUsdAndBsSAliases()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = await AddSupplierAsync(context);
        var service = CreateService(context);

        var usdInvoice = await service.StageAsync(CreateStageRequest(
            supplier.Id,
            CurrencyCodes.Usd,
            99m,
            new StageLineDto(null, null, "Usd product", 1m, 10m)));
        var aliasInvoice = await service.StageAsync(CreateStageRequest(
            supplier.Id,
            "VES",
            50m,
            new StageLineDto(null, null, "Local product", 1m, 1000m)));

        Assert.Equal(CurrencyCodes.Usd, usdInvoice.Currency);
        Assert.Equal(1m, usdInvoice.AppliedRate);
        Assert.Equal(10m, Assert.Single(usdInvoice.Lines).UnitCostUSD);
        Assert.Equal(CurrencyCodes.BsS, aliasInvoice.Currency);
        Assert.Equal(50m, aliasInvoice.AppliedRate);
        Assert.Equal(20m, Assert.Single(aliasInvoice.Lines).UnitCostUSD);
    }

    [Fact]
    public async Task ConfirmAsync_SingleCop_UsesIdentityRateAndNeverReadsBcv()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var product = AddProduct(context, "SKU-CONFIRM-SINGLE", costUsd: 0m, stock: 0m);
        var invoice = await AddInvoiceAsync(
            context,
            "COP",
            1m,
            1000m,
            1000m,
            product);
        var line = Assert.Single(invoice.Lines);
        var bcv = CreateThrowingBcvProvider();
        var service = CreateService(context, profile: CreateSingleProfileMock("COP").Object, bcv: bcv.Object);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(
            new[] { new ConfirmLineDto(line.Id, true, null, null) },
            AppliedRate: 4000m));

        var savedProduct = await context.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await context.SupplierInvoices.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(1000m, savedProduct.CostPriceUSD);
        Assert.Equal(1000m, savedInvoice.Lines.Single().UnitCostUSD);
        Assert.Equal(1m, savedInvoice.AppliedRate);
        Assert.Equal("COP", savedInvoice.Currency);
        bcv.Verify(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmAsync_SingleCop_RejectsTargetOutsidePrimary()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var product = AddProduct(context, "SKU-CONFIRM-SINGLE-USD", costUsd: 0m, stock: 0m);
        var invoice = await AddInvoiceAsync(
            context,
            "COP",
            1m,
            1000m,
            1000m,
            product);
        var line = Assert.Single(invoice.Lines);
        var service = CreateService(context, profile: CreateSingleProfileMock("COP").Object);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(line.Id, true, null, null) },
                Currency: CurrencyCodes.Usd)));

        Assert.Contains("must be COP", exception.Message, StringComparison.Ordinal);
        var savedInvoice = await context.SupplierInvoices.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
        Assert.Equal("COP", savedInvoice.Currency);
    }

    [Fact]
    public async Task ConfirmAsync_DualCop_ValidatesManualPairAndReconcilesCosts()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var product = AddProduct(context, "SKU-CONFIRM-COP", costUsd: 0m, stock: 0m);
        var invoice = await AddInvoiceAsync(
            context,
            "COP",
            4000.1235m,
            1000m,
            PricingCalculator.CalculateInvoiceUnitCostUsd(1000m, 1m, "COP", 4000.1235m),
            product);
        var line = Assert.Single(invoice.Lines);
        var manualRates = CreateManualRateMock("COP", CopQuote);
        var bcv = CreateThrowingBcvProvider();
        var service = CreateService(
            context,
            profile: CreateDualProfileMock("COP").Object,
            manualRates: manualRates.Object,
            bcv: bcv.Object);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(
            new[] { new ConfirmLineDto(line.Id, true, null, null) }));

        var savedProduct = await context.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await context.SupplierInvoices.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(0.25m, savedProduct.CostPriceUSD);
        Assert.Equal(0.25m, savedInvoice.Lines.Single().UnitCostUSD);
        Assert.Equal(4000.1235m, savedInvoice.AppliedRate);
        manualRates.Verify(
            serviceMock => serviceMock.GetRateAsync("USD", "COP", It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        bcv.Verify(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmAsync_DualCop_ManualRateOutsideToleranceIsRejected()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var product = AddProduct(context, "SKU-CONFIRM-COP-DEVIATION", costUsd: 4m, stock: 6m);
        var invoice = await AddInvoiceAsync(
            context,
            "COP",
            5000m,
            1000m,
            0.20m,
            product);
        var line = Assert.Single(invoice.Lines);
        var service = CreateService(
            context,
            profile: CreateDualProfileMock("COP").Object,
            manualRates: CreateManualRateMock("COP", CopRate).Object,
            bcv: CreateThrowingBcvProvider().Object);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(line.Id, true, null, null) })));

        Assert.Contains("tasa manual del par USD/COP", exception.Message, StringComparison.Ordinal);
        var savedProduct = await context.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await context.SupplierInvoices.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(4m, savedProduct.CostPriceUSD);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
    }

    [Fact]
    public async Task ConfirmAsync_DualCop_MissingManualQuoteFailsClosed()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var product = AddProduct(context, "SKU-CONFIRM-COP-MISSING", costUsd: 4m, stock: 6m);
        var invoice = await AddInvoiceAsync(
            context,
            "COP",
            CopRate,
            1000m,
            0.25m,
            product);
        var line = Assert.Single(invoice.Lines);
        var manualRates = new Mock<ICurrencyRateService>();
        manualRates
            .Setup(serviceMock => serviceMock.GetRateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateOnly?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No hay tasa manual configurada para USD->COP."));
        var bcv = CreateThrowingBcvProvider();
        var service = CreateService(
            context,
            profile: CreateDualProfileMock("COP").Object,
            manualRates: manualRates.Object,
            bcv: bcv.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(line.Id, true, null, null) })));

        Assert.Contains("tasa manual", exception.Message, StringComparison.Ordinal);
        var savedProduct = await context.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await context.SupplierInvoices.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(4m, savedProduct.CostPriceUSD);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
        bcv.Verify(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmAsync_DualCop_ChangingCurrencyWithoutRateFailsClosed()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var product = AddProduct(context, "SKU-CONFIRM-COP-NORATE", costUsd: 4m, stock: 6m);
        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            1000m,
            1000m,
            product);
        var line = Assert.Single(invoice.Lines);
        var service = CreateService(
            context,
            profile: CreateDualProfileMock("COP").Object,
            manualRates: CreateManualRateMock("COP", CopQuote).Object);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(line.Id, true, null, null) },
                Currency: "COP")));

        Assert.StartsWith(
            "An applied exchange rate greater than zero is required for supplier invoices in COP.",
            exception.Message);
        var savedInvoice = await context.SupplierInvoices.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
        Assert.Equal(CurrencyCodes.Usd, savedInvoice.Currency);
    }

    [Fact]
    public async Task ConfirmAsync_DualCop_RejectsCurrencyOutsideProfile()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var product = AddProduct(context, "SKU-CONFIRM-COP-EUR", costUsd: 4m, stock: 6m);
        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            1000m,
            1000m,
            product);
        var line = Assert.Single(invoice.Lines);
        var service = CreateService(context, profile: CreateDualProfileMock("COP").Object);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(line.Id, true, null, null) },
                Currency: "EUR")));

        Assert.Contains("must be USD or COP", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAsync_DualVes_KeepsBcvValidationAndSnapshotsFourDecimals()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var product = AddProduct(context, "SKU-CONFIRM-VES", costUsd: 0m, stock: 0m);
        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.BsS,
            50m,
            1000m,
            20m,
            product);
        var line = Assert.Single(invoice.Lines);
        var bcv = new Mock<ITodayExchangeRateProvider>();
        bcv.Setup(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        var service = CreateService(context, profile: CreateDualProfileMock("VES").Object, bcv: bcv.Object);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(
            new[] { new ConfirmLineDto(line.Id, true, null, null) },
            AppliedRate: 51m));

        var savedProduct = await context.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await context.SupplierInvoices.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(PricingCalculator.ToUSD(1000m, 51m), savedProduct.CostPriceUSD);
        Assert.Equal(51m, savedInvoice.AppliedRate);
        Assert.Equal(CurrencyCodes.BsS, savedInvoice.Currency);
        bcv.Verify(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmAsync_DualVes_UsdTargetForcesRateOneWithoutBcv()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var product = AddProduct(context, "SKU-CONFIRM-VES-USD", costUsd: 0m, stock: 0m);
        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            1000m,
            1000m,
            product);
        var line = Assert.Single(invoice.Lines);
        var bcv = CreateThrowingBcvProvider();
        var service = CreateService(context, profile: CreateDualProfileMock("VES").Object, bcv: bcv.Object);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(
            new[] { new ConfirmLineDto(line.Id, true, null, null) },
            AppliedRate: 50m));

        var savedProduct = await context.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await context.SupplierInvoices.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(1000m, savedProduct.CostPriceUSD);
        Assert.Equal(1m, savedInvoice.AppliedRate);
        bcv.Verify(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmAsync_LegacyUsdInvoice_ReconcilesSnapshotAndCost()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var product = AddProduct(context, "SKU-CONFIRM-LEGACY-USD", costUsd: 0m, stock: 0m);
        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            12.5m,
            12.5m,
            product);
        var line = Assert.Single(invoice.Lines);
        var service = CreateService(context);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(
            new[] { new ConfirmLineDto(line.Id, true, null, null) }));

        var savedProduct = await context.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id);
        var savedInvoice = await context.SupplierInvoices.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(12.5m, savedInvoice.Lines.Single().UnitCostDocument);
        Assert.Equal(12.5m, savedInvoice.Lines.Single().UnitCostUSD);
        Assert.Equal(12.5m, savedProduct.CostPriceUSD);
        Assert.Equal(1m, savedInvoice.AppliedRate);
        Assert.Equal(CurrencyCodes.Usd, savedInvoice.Currency);
    }

    private static SupplierInvoiceService CreateService(
        InventoryDbContext context,
        IMonetaryProfileService? profile = null,
        ICurrencyRateService? manualRates = null,
        ITodayExchangeRateProvider? bcv = null)
    {
        var settings = new Mock<ISystemSettingsService>();
        settings
            .Setup(service => service.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync(0.30d.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var similaritySearch = new Mock<ISupplierProductSimilaritySearch>();
        similaritySearch
            .Setup(search => search.FindCandidatesAsync(
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SupplierProductSimilarityCandidate>());

        return new SupplierInvoiceService(
            context,
            settings.Object,
            similaritySearch.Object,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IProductManagementService>(),
            memoryCache: null,
            logger: null,
            rateProvider: bcv,
            monetaryProfileService: profile,
            currencyRateService: manualRates);
    }

    private static Mock<IMonetaryProfileService> CreateSingleProfileMock(string primaryCode)
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = primaryCode,
                PrimaryCurrencySymbol = primaryCode,
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null,
                IsConfigured = true
            });
        return mock;
    }

    private static Mock<IMonetaryProfileService> CreateDualProfileMock(
        string secondaryCode,
        string primaryCode = "USD")
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = primaryCode,
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = secondaryCode,
                SecondaryCurrencySymbol = secondaryCode == "VES" ? CurrencyCodes.BsS : secondaryCode,
                IsConfigured = true
            });
        return mock;
    }

    private static Mock<ICurrencyRateService> CreateManualRateMock(string secondaryCode, decimal rate)
    {
        var mock = new Mock<ICurrencyRateService>();
        mock
            .Setup(service => service.GetRateAsync(
                "USD",
                secondaryCode,
                It.IsAny<DateOnly?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(
                rate,
                CurrencyRateSource.Manual,
                TimeZoneHelper.GetVenezuelaDate(),
                "USD",
                secondaryCode));
        return mock;
    }

    private static Mock<ITodayExchangeRateProvider> CreateThrowingBcvProvider()
    {
        var mock = new Mock<ITodayExchangeRateProvider>();
        mock
            .Setup(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BCV must not be called for this profile."));
        return mock;
    }

    private static async Task<Supplier> AddSupplierAsync(
        InventoryDbContext context,
        string? invoiceCurrency = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var supplier = new Supplier
        {
            CommercialName = $"Profile Invoice Supplier {suffix}",
            NormalizedCommercialName = $"PROFILE INVOICE SUPPLIER {suffix}",
            InvoiceCurrency = invoiceCurrency
        };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        return supplier;
    }

    private static Product AddProduct(
        InventoryDbContext context,
        string sku,
        decimal costUsd,
        decimal stock = 5m)
    {
        var product = new Product
        {
            Name = sku,
            SKU = sku,
            CostPriceUSD = costUsd,
            Cost = costUsd,
            ProfitMarginRetail = 20m,
            ProfitMarginWholesale = 20m,
            ProfitPercentage = 20m,
            PriceUSD = costUsd,
            PriceRetailUSD = costUsd,
            PriceWholesaleUSD = costUsd,
            StockQuantity = stock
        };
        context.Products.Add(product);
        return product;
    }

    private static async Task AddProductAsync(
        InventoryDbContext context,
        string sku,
        decimal costUsd)
    {
        AddProduct(context, sku, costUsd);
        await context.SaveChangesAsync();
    }

    private static StageSupplierInvoiceRequestDto CreateStageRequest(
        int supplierId,
        string currency,
        decimal appliedRate,
        params StageLineDto[] lines) => new(
        supplierId,
        null,
        null,
        new SupplierColumnMappingDto("Barcode", "SupplierCode", "Name", "Quantity", "UnitCost"),
        lines,
        currency,
        appliedRate);

    private static async Task<SupplierInvoice> AddInvoiceAsync(
        InventoryDbContext context,
        string currency,
        decimal appliedRate,
        decimal unitCostDocument,
        decimal unitCostUsd,
        params Product[] products)
    {
        var invoice = new SupplierInvoice
        {
            Supplier = new Supplier
            {
                CommercialName = $"Confirm Profile Supplier {Guid.NewGuid():N}",
                NormalizedCommercialName = $"CONFIRM PROFILE SUPPLIER {Guid.NewGuid():N}"
            },
            Status = SupplierInvoiceStatus.Draft,
            Currency = currency,
            AppliedRate = appliedRate,
            Lines = products.Select(product => new SupplierInvoiceLine
            {
                SupplierCode = product.SKU,
                Quantity = 1m,
                UnitCostDocument = unitCostDocument,
                UnitCostUSD = unitCostUsd,
                Status = SupplierInvoiceLineStatus.Update,
                ResolvedProduct = product,
                ResolvedProductId = product.Id,
                MatchMethod = MatchMethod.Barcode
            }).ToList()
        };

        context.SupplierInvoices.Add(invoice);
        await context.SaveChangesAsync();
        return invoice;
    }
}
