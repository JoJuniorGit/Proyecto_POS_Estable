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
    public async Task CreateProductFromLineAsync_ResolvedLine_CreatesNewProductAndRepointsWithoutTouchingPrevious()
    {
        var (service, context) = CreateService();
        var previousProduct = new ProductBuilder()
            .WithSku("RESOLVED-SKU")
            .WithName("Producto Previo")
            .WithCostAndMargin(9.99m, 45m)
            .WithStock(7m)
            .Build();
        context.Products.Add(previousProduct);
        await context.SaveChangesAsync();
        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            CreateLine(
                supplierCode: "RE-1",
                name: "FILA PREVIA",
                quantity: 3m,
                unitCostUsd: 12m,
                status: SupplierInvoiceLineStatus.Update,
                resolvedProductId: previousProduct.Id,
                marginRetailOverride: 30m));
        var line = Assert.Single(invoice.Lines);

        var result = await service.CreateProductFromLineAsync(
            invoice.Id,
            line.Id,
            new CreateInvoiceProductRequestDto(ValidBarcode, "  Producto Re-Resuelto  "));

        var created = await context.Products.AsNoTracking().SingleAsync(product => product.SKU == ValidBarcode);
        Assert.NotEqual(previousProduct.Id, created.Id);
        Assert.Equal("Producto Re-Resuelto", created.Name);

        var savedLine = await context.SupplierInvoiceLines.AsNoTracking().SingleAsync(candidate => candidate.Id == line.Id);
        Assert.Equal(created.Id, savedLine.ResolvedProductId);
        Assert.Equal("Producto Re-Resuelto", savedLine.Name);
        Assert.Equal(SupplierInvoiceLineStatus.Update, savedLine.Status);
        Assert.Equal(0m, savedLine.OldCostPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(12m * 1.30m), savedLine.SuggestedRetailPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(12m * 1.30m), savedLine.SuggestedWholesalePriceUSD);

        // El producto del vínculo anterior no se toca.
        var savedPrevious = await context.Products.AsNoTracking().SingleAsync(product => product.Id == previousProduct.Id);
        Assert.Equal(9.99m, savedPrevious.CostPriceUSD);
        Assert.Equal(45m, savedPrevious.ProfitMarginRetail);

        var alias = await context.SupplierProductCodes.AsNoTracking().SingleAsync();
        Assert.Equal("RE-1", alias.Code);
        Assert.Equal(created.Id, alias.ProductId);

        Assert.Equal(created.Id, Assert.Single(result.Lines).ResolvedProductId);
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

    [Fact]
    public async Task LinkProductToLineAsync_LinksExistingProductUpdatesStatusLoadsOldCostAndPersistsAliases()
    {
        var (service, context) = CreateService();
        var existingProduct = new ProductBuilder()
            .WithSku("7590001112223")
            .WithName("Harina P.A.N. 1kg")
            .WithCostAndMargin(8.50m, 30m)
            .WithWholesale(6m, 15m)
            .WithStock(12m)
            .Build();

        context.Products.Add(existingProduct);
        await context.SaveChangesAsync();

        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            CreateLine(
                supplierCode: "PROV-HAR-01",
                name: "HARINA MAIZ BLANCO 1KG",
                quantity: 4m,
                unitCostUsd: 10.25m));
        var line = Assert.Single(invoice.Lines);

        var result = await service.LinkProductToLineAsync(
            invoice.Id,
            line.Id,
            new LinkInvoiceProductRequestDto(existingProduct.Id, "HARINA MAIZ BLANCO 1KG"));

        var resultLine = Assert.Single(result.Lines);
        Assert.Equal(existingProduct.Id, resultLine.ResolvedProductId);
        Assert.Equal("Update", resultLine.Status);
        Assert.Equal("Harina P.A.N. 1kg", resultLine.Name);
        Assert.Equal(8.50m, resultLine.OldCostPriceUSD);
        Assert.Equal(30m, resultLine.OldProfitMarginRetail);
        Assert.Equal(15m, resultLine.OldProfitMarginWholesale);
        Assert.Equal(12m, resultLine.OldStockQuantity);
        Assert.Equal(PricingCalculator.RoundPriceUp(10.25m * 1.30m), resultLine.SuggestedRetailPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(10.25m * 1.15m), resultLine.SuggestedWholesalePriceUSD);
        Assert.True(resultLine.IsApproved);

        var aliases = await context.SupplierProductCodes
            .AsNoTracking()
            .Where(alias => alias.SupplierId == invoice.SupplierId && alias.ProductId == existingProduct.Id)
            .Select(alias => alias.Code)
            .ToListAsync();
        Assert.Contains("PROV-HAR-01", aliases);
        Assert.Contains("HARINA MAIZ BLANCO 1KG", aliases);
    }

    [Fact]
    public async Task LinkProductToLineAsync_LearnedAlias_AutomaticallyMatchesFutureInvoicesByDescription()
    {
        var (service, context) = CreateService();
        var existingProduct = new ProductBuilder()
            .WithSku("7590009998887")
            .WithName("Café Madrid 500g")
            .WithCostAndMargin(4.20m, 25m)
            .WithStock(8m)
            .Build();
        context.Products.Add(existingProduct);
        await context.SaveChangesAsync();

        var firstInvoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            CreateLine(
                supplierCode: null,
                name: "CAFE MOLIDO MADRID MEDIO KILO",
                quantity: 5m,
                unitCostUsd: 4.80m));
        var firstLine = Assert.Single(firstInvoice.Lines);

        await service.LinkProductToLineAsync(
            firstInvoice.Id,
            firstLine.Id,
            new LinkInvoiceProductRequestDto(existingProduct.Id, "CAFE MOLIDO MADRID MEDIO KILO"));

        // Stage a future invoice from the same supplier with only the OCR description (no barcode/supplier code)
        var secondInvoice = await service.StageAsync(new StageSupplierInvoiceRequestDto(
            firstInvoice.SupplierId,
            null,
            firstInvoice.Supplier!.CommercialName,
            null,
            new[] { new StageLineDto(null, null, "CAFE MOLIDO MADRID MEDIO KILO", 3m, 5.00m) },
            CurrencyCodes.Usd,
            1m,
            OcrSourced: true));

        var matchedLine = Assert.Single(secondInvoice.Lines);
        Assert.Equal(existingProduct.Id, matchedLine.ResolvedProductId);
        Assert.Equal("Update", matchedLine.Status);
        Assert.Equal("SupplierCode", matchedLine.MatchMethod);
        Assert.Equal(4.20m, matchedLine.OldCostPriceUSD);
    }

    [Fact]
    public async Task LinkProductToLineAsync_WithMultipleProducts_ExpandsLinesLoadsEachOldCostAndPersistsMultiAliases()
    {
        var (service, context) = CreateService();
        var productA = new ProductBuilder()
            .WithId(0)
            .WithSku("7590001000011")
            .WithName("Refresco Cola 2L")
            .WithCostAndMargin(1.20m, 30m)
            .WithStock(10m)
            .Build();
        var productB = new ProductBuilder()
            .WithId(0)
            .WithSku("7590001000028")
            .WithName("Refresco Naranja 2L")
            .WithCostAndMargin(1.10m, 25m)
            .WithStock(5m)
            .Build();
        var productC = new ProductBuilder()
            .WithId(0)
            .WithSku("7590001000035")
            .WithName("Refresco Limón 2L")
            .WithCostAndMargin(1.15m, 20m)
            .WithStock(8m)
            .Build();
        context.Products.AddRange(productA, productB, productC);
        await context.SaveChangesAsync();

        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            CreateLine(
                supplierCode: "PROV-REF-2L",
                name: "REFRESCO SURTIDO 2 LITROS",
                quantity: 6m,
                unitCostUsd: 1.50m));
        var line = Assert.Single(invoice.Lines);

        var result = await service.LinkProductToLineAsync(
            invoice.Id,
            line.Id,
            new LinkInvoiceProductRequestDto(
                productA.Id,
                "REFRESCO SURTIDO 2 LITROS",
                new[] { productA.Id, productB.Id, productC.Id }));

        Assert.Equal(3, result.Lines.Count);

        Assert.Equal(productA.Id, result.Lines[0].ResolvedProductId);
        Assert.Equal("Refresco Cola 2L", result.Lines[0].Name);
        Assert.Equal(6m, result.Lines[0].Quantity);
        Assert.Equal(1.50m, result.Lines[0].UnitCostUSD);
        Assert.Equal(1.20m, result.Lines[0].OldCostPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(1.50m * 1.30m), result.Lines[0].SuggestedRetailPriceUSD);

        Assert.Equal(productB.Id, result.Lines[1].ResolvedProductId);
        Assert.Equal("Refresco Naranja 2L", result.Lines[1].Name);
        Assert.Equal(0m, result.Lines[1].Quantity);
        Assert.Equal(1.50m, result.Lines[1].UnitCostUSD);
        Assert.Equal(1.10m, result.Lines[1].OldCostPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(1.50m * 1.25m), result.Lines[1].SuggestedRetailPriceUSD);

        Assert.Equal(productC.Id, result.Lines[2].ResolvedProductId);
        Assert.Equal("Refresco Limón 2L", result.Lines[2].Name);
        Assert.Equal(0m, result.Lines[2].Quantity);
        Assert.Equal(1.50m, result.Lines[2].UnitCostUSD);
        Assert.Equal(1.15m, result.Lines[2].OldCostPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(1.50m * 1.20m), result.Lines[2].SuggestedRetailPriceUSD);

        var codeAliases = await context.SupplierProductCodes
            .AsNoTracking()
            .Where(alias => alias.SupplierId == invoice.SupplierId && alias.Code == "PROV-REF-2L")
            .Select(alias => alias.ProductId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { productA.Id, productB.Id, productC.Id }.OrderBy(id => id), codeAliases);
    }

    [Fact]
    public async Task LinkProductToLineAsync_MultiProductAlias_ExpandsFutureInvoiceAndConfirmSkipsZeroQuantityStockMovement()
    {
        var (service, context) = CreateService();
        var productA = new ProductBuilder()
            .WithId(0)
            .WithSku("7590002000010")
            .WithName("Jugo Durazno 1L")
            .WithCostAndMargin(2.00m, 30m)
            .WithStock(10m)
            .Build();
        var productB = new ProductBuilder()
            .WithId(0)
            .WithSku("7590002000027")
            .WithName("Jugo Manzana 1L")
            .WithCostAndMargin(2.10m, 25m)
            .WithStock(7m)
            .Build();
        context.Products.AddRange(productA, productB);
        await context.SaveChangesAsync();

        var firstInvoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            CreateLine(
                supplierCode: null,
                name: "JUGO FRUTAS 1L SURTIDO",
                quantity: 4m,
                unitCostUsd: 2.50m));
        var firstLine = Assert.Single(firstInvoice.Lines);

        await service.LinkProductToLineAsync(
            firstInvoice.Id,
            firstLine.Id,
            new LinkInvoiceProductRequestDto(
                productA.Id,
                "JUGO FRUTAS 1L SURTIDO",
                new[] { productA.Id, productB.Id }));

        var secondInvoice = await service.StageAsync(new StageSupplierInvoiceRequestDto(
            firstInvoice.SupplierId,
            null,
            firstInvoice.Supplier!.CommercialName,
            null,
            new[] { new StageLineDto(null, null, "JUGO FRUTAS 1L SURTIDO", 5m, 3.00m) },
            CurrencyCodes.Usd,
            1m,
            OcrSourced: true));

        Assert.Equal(2, secondInvoice.Lines.Count);
        Assert.Equal(productA.Id, secondInvoice.Lines[0].ResolvedProductId);
        Assert.Equal(5m, secondInvoice.Lines[0].Quantity);
        Assert.Equal(productB.Id, secondInvoice.Lines[1].ResolvedProductId);
        Assert.Equal(0m, secondInvoice.Lines[1].Quantity);

        await service.ConfirmAsync(
            secondInvoice.Id,
            new ConfirmSupplierInvoiceRequestDto(
                secondInvoice.Lines
                    .Select(line => new ConfirmLineDto(line.Id, true, line.MarginRetailOverride, line.MarginWholesaleOverride))
                    .ToArray()));

        var updatedA = await context.Products.AsNoTracking().SingleAsync(p => p.Id == productA.Id);
        var updatedB = await context.Products.AsNoTracking().SingleAsync(p => p.Id == productB.Id);

        Assert.Equal(3.00m, updatedA.CostPriceUSD);
        Assert.Equal(15m, updatedA.StockQuantity);

        Assert.Equal(3.00m, updatedB.CostPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(3.00m * 1.25m), updatedB.PriceRetailUSD);
        Assert.Equal(7m, updatedB.StockQuantity);

        var movements = await context.StockMovements.AsNoTracking().ToListAsync();
        var movement = Assert.Single(movements);
        Assert.Equal(productA.Id, movement.ProductId);
        Assert.Equal(5m, movement.QuantityChange);

        var persistedAliases = await context.SupplierProductCodes
            .AsNoTracking()
            .Where(alias => alias.SupplierId == firstInvoice.SupplierId && alias.Code == "JUGO FRUTAS 1L SURTIDO")
            .Select(alias => alias.ProductId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { productA.Id, productB.Id }.OrderBy(id => id), persistedAliases);
    }

    [Fact]
    public async Task LinkProductToLineAsync_WithCustomMarginOverrides_AppliesSuggestedPricesAndPersistsOnConfirm()
    {
        var (service, context) = CreateService();
        var productA = new ProductBuilder()
            .WithId(0)
            .WithSku("7590003000019")
            .WithName("Galleta Vainilla Paquete")
            .WithCostAndMargin(1.00m, 25m)
            .WithStock(10m)
            .Build();
        var productB = new ProductBuilder()
            .WithId(0)
            .WithSku("7590003000026")
            .WithName("Galleta Chocolate Paquete")
            .WithCostAndMargin(1.10m, 20m)
            .WithStock(6m)
            .Build();
        context.Products.AddRange(productA, productB);
        await context.SaveChangesAsync();

        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            CreateLine(
                supplierCode: "PROV-GALLETA-SURTIDA",
                name: "GALLETA SURTIDA PAQUETE",
                quantity: 8m,
                unitCostUsd: 2.00m));
        var line = Assert.Single(invoice.Lines);

        var linked = await service.LinkProductToLineAsync(
            invoice.Id,
            line.Id,
            new LinkInvoiceProductRequestDto(
                productA.Id,
                "GALLETA SURTIDA PAQUETE",
                new[] { productA.Id, productB.Id },
                new[]
                {
                    new LinkedProductMarginOverrideDto(productA.Id, 40m, 22.5m),
                    new LinkedProductMarginOverrideDto(productB.Id, 35m, 18m)
                }));

        Assert.Equal(2, linked.Lines.Count);
        Assert.Equal(40m, linked.Lines[0].MarginRetailOverride);
        Assert.Equal(22.5m, linked.Lines[0].MarginWholesaleOverride);
        Assert.Equal(PricingCalculator.RoundPriceUp(2.00m * 1.40m), linked.Lines[0].SuggestedRetailPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(2.00m * 1.225m), linked.Lines[0].SuggestedWholesalePriceUSD);

        Assert.Equal(35m, linked.Lines[1].MarginRetailOverride);
        Assert.Equal(18m, linked.Lines[1].MarginWholesaleOverride);
        Assert.Equal(PricingCalculator.RoundPriceUp(2.00m * 1.35m), linked.Lines[1].SuggestedRetailPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(2.00m * 1.18m), linked.Lines[1].SuggestedWholesalePriceUSD);

        await service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(
                linked.Lines
                    .Select(item => new ConfirmLineDto(item.Id, true, item.MarginRetailOverride, item.MarginWholesaleOverride))
                    .ToArray()));

        var savedA = await context.Products.AsNoTracking().SingleAsync(p => p.Id == productA.Id);
        var savedB = await context.Products.AsNoTracking().SingleAsync(p => p.Id == productB.Id);

        Assert.Equal(40m, savedA.ProfitMarginRetail);
        Assert.Equal(22.5m, savedA.ProfitMarginWholesale);
        Assert.True(savedA.HasWholesale);
        Assert.Equal(PricingCalculator.RoundPriceUp(2.00m * 1.40m), savedA.PriceRetailUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(2.00m * 1.225m), savedA.PriceWholesaleUSD);

        Assert.Equal(35m, savedB.ProfitMarginRetail);
        Assert.Equal(18m, savedB.ProfitMarginWholesale);
        Assert.True(savedB.HasWholesale);
        Assert.Equal(PricingCalculator.RoundPriceUp(2.00m * 1.35m), savedB.PriceRetailUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(2.00m * 1.18m), savedB.PriceWholesaleUSD);
    }

    [Fact]
    public async Task SaveSupplierOcrTemplateAsync_AndGetSupplierOcrTemplateAsync_PersistsAndUpdatesPerSupplierTemplate()
    {
        var (service, context) = CreateService();
        var supplier = new Supplier
        {
            RifOrNit = "J-55555555-5",
            NormalizedRifOrNit = "J555555555",
            CommercialName = "Distribuidora OCR",
            NormalizedCommercialName = "DISTRIBUIDORA OCR"
        };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        Assert.Null(await service.GetSupplierOcrTemplateAsync(supplier.Id));

        var initialTemplate = new SupplierOcrTemplateDto(
            RotationDegrees: 90,
            ContrastFactor: 1.35,
            BrightnessDelta: -12,
            BinarizationThreshold: 140,
            RoiX: 0.05,
            RoiY: 0.12,
            RoiWidth: 0.90,
            RoiHeight: 0.75,
            HeaderAnchorKeywords: "RENGLON, DETALLE",
            FooterAnchorKeywords: "SUBTOTAL, IVA",
            ProductColumnPosition: 3,
            QuantityColumnPosition: 2,
            UnitCostColumnPosition: 4,
            SupplierCodeColumnPosition: 1,
            CostAmountRule: OcrCostAmountRule.LineTotal,
            DecimalSeparator: OcrDecimalSeparatorRule.Comma,
            CharacterReplacements: new[]
            {
                new OcrCharacterReplacementRuleDto("O", "0")
            },
            FactorExtractionPattern: "AUTO");

        var saved = await service.SaveSupplierOcrTemplateAsync(supplier.Id, initialTemplate);
        Assert.Equal(0.05, saved.RoiX);
        Assert.Equal(90, saved.RotationDegrees);
        Assert.Equal(OcrCostAmountRule.LineTotal, saved.CostAmountRule);
        Assert.Equal(OcrDecimalSeparatorRule.Comma, saved.DecimalSeparator);
        Assert.Single(saved.CharacterReplacements!);

        var updatedTemplate = initialTemplate with
        {
            RotationDegrees = 0,
            BinarizationThreshold = 0,
            CostAmountRule = OcrCostAmountRule.UnitCost,
            FactorExtractionPattern = "BTO_X_N"
        };

        var updated = await service.SaveSupplierOcrTemplateAsync(supplier.Id, updatedTemplate);
        Assert.Equal(0, updated.RotationDegrees);
        Assert.Equal(0, updated.BinarizationThreshold);
        Assert.Equal(OcrCostAmountRule.UnitCost, updated.CostAmountRule);
        Assert.Equal("BTO_X_N", updated.FactorExtractionPattern);

        var loaded = await service.GetSupplierOcrTemplateAsync(supplier.Id);
        Assert.NotNull(loaded);
        Assert.Equal("BTO_X_N", loaded!.FactorExtractionPattern);
        Assert.Single(await context.SupplierOcrTemplates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StageAsync_WithStageLineConversionFactor_PersistsConversionFactorOnStagedInvoiceLines()
    {
        var (service, context) = CreateService();
        var product = new ProductBuilder()
            .WithSku("7591234567890")
            .WithName("Harina P.A.N. 1kg")
            .WithCostAndMargin(1.00m, 30m)
            .WithStock(10m)
            .Build();
        var supplier = new Supplier
        {
            RifOrNit = "J-88888888-8",
            NormalizedRifOrNit = "J888888888",
            CommercialName = "Mayorista Pack",
            NormalizedCommercialName = "MAYORISTA PACK"
        };
        context.Products.Add(product);
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var staged = await service.StageAsync(new StageSupplierInvoiceRequestDto(
            supplier.Id,
            supplier.RifOrNit,
            supplier.CommercialName,
            null,
            new[]
            {
                new StageLineDto("COD-1", "7591234567890", "HARINA PAN 1X40 UND", 2m, 40m, ConversionFactor: 40m)
            },
            CurrencyCodes.Usd,
            1m,
            OcrSourced: true));

        var line = Assert.Single(staged.Lines);
        Assert.Equal(40m, line.ConversionFactor);
    }


    [Fact]
    public async Task LinkProductToLineAsync_ResolvedLine_ReLinksRefreshesSnapshotsAndMargins()
    {
        var (service, context) = CreateService();
        var previousProduct = new ProductBuilder()
            .WithId(0)
            .WithSku("7591111111111")
            .WithName("Producto Anterior")
            .WithCostAndMargin(8.50m, 30m)
            .WithStock(12m)
            .Build();
        var newProduct = new ProductBuilder()
            .WithId(0)
            .WithSku("7592222222222")
            .WithName("Producto Nuevo")
            .WithCostAndMargin(5.00m, 20m)
            .WithWholesale(6m, 10m)
            .WithStock(3m)
            .Build();
        context.Products.AddRange(previousProduct, newProduct);
        await context.SaveChangesAsync();

        var invoice = await AddInvoiceAsync(
            context,
            CurrencyCodes.Usd,
            1m,
            CreateLine(
                supplierCode: "PROV-1",
                name: "DESCRIPCION ANTERIOR",
                quantity: 4m,
                unitCostUsd: 10.25m,
                status: SupplierInvoiceLineStatus.Update,
                resolvedProductId: previousProduct.Id));
        var line = Assert.Single(invoice.Lines);

        var result = await service.LinkProductToLineAsync(
            invoice.Id,
            line.Id,
            new LinkInvoiceProductRequestDto(newProduct.Id, "DESCRIPCION EDITADA"));

        var resultLine = Assert.Single(result.Lines);
        Assert.Equal(newProduct.Id, resultLine.ResolvedProductId);
        Assert.Equal("Producto Nuevo", resultLine.Name);
        Assert.Equal("Update", resultLine.Status);
        Assert.Equal(5.00m, resultLine.OldCostPriceUSD);
        Assert.Equal(20m, resultLine.OldProfitMarginRetail);
        Assert.Equal(10m, resultLine.OldProfitMarginWholesale);
        Assert.Equal(3m, resultLine.OldStockQuantity);
        Assert.Equal(PricingCalculator.RoundPriceUp(10.25m * 1.20m), resultLine.SuggestedRetailPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(10.25m * 1.10m), resultLine.SuggestedWholesalePriceUSD);
        Assert.True(resultLine.IsApproved);

        var aliases = await context.SupplierProductCodes.AsNoTracking().ToListAsync();
        Assert.Equal(2, aliases.Count);
        Assert.Contains(aliases, alias => alias.Code == "PROV-1" && alias.ProductId == newProduct.Id);
        Assert.Contains(aliases, alias => alias.Code == "DESCRIPCION EDITADA" && alias.ProductId == newProduct.Id);
        // 8.175-S5: el nombre del vínculo previo (line.Name) nunca se re-apunta como alias.
        Assert.DoesNotContain(aliases, alias => alias.Code == "DESCRIPCION ANTERIOR");
        Assert.DoesNotContain(aliases, alias => alias.ProductId == previousProduct.Id);

        // El producto anterior no sufre mutaciones durante la re-resolución.
        var savedPrevious = await context.Products.AsNoTracking().SingleAsync(product => product.Id == previousProduct.Id);
        Assert.Equal(8.50m, savedPrevious.CostPriceUSD);
        Assert.Equal(30m, savedPrevious.ProfitMarginRetail);
    }

    [Fact]
    public async Task CreateProductFromLineAsync_ResolvedLine_CreatesRepointsAndRemovesReplacedRows()
    {
        var (service, context) = CreateService();
        var previousProduct = new ProductBuilder()
            .WithId(0)
            .WithSku("7596666666661")
            .WithName("Vinculo Anterior")
            .WithCostAndMargin(6.00m, 30m)
            .WithStock(9m)
            .Build();
        var extraProduct = new ProductBuilder()
            .WithId(0)
            .WithSku("7596666666670")
            .WithName("Vinculo Extra")
            .WithCostAndMargin(6.50m, 25m)
            .WithStock(4m)
            .Build();
        context.Products.AddRange(previousProduct, extraProduct);
        await context.SaveChangesAsync();

        var anchorLine = CreateLine(
            supplierCode: "CRE-1",
            name: "FILA ANCLA",
            quantity: 2m,
            unitCostUsd: 10m,
            status: SupplierInvoiceLineStatus.Update,
            resolvedProductId: previousProduct.Id,
            marginRetailOverride: 30m);
        var extraLine = CreateLine(
            supplierCode: "CRE-1",
            name: "FILA EXTRA",
            quantity: 0m,
            unitCostUsd: 10m,
            status: SupplierInvoiceLineStatus.Update,
            resolvedProductId: extraProduct.Id);
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, anchorLine, extraLine);

        var result = await service.CreateProductFromLineAsync(
            invoice.Id,
            anchorLine.Id,
            new CreateInvoiceProductRequestDto(ValidBarcode, "Producto Nuevo Creado", new[] { extraLine.Id }));

        var created = await context.Products.AsNoTracking().SingleAsync(product => product.SKU == ValidBarcode);
        Assert.Equal("Producto Nuevo Creado", created.Name);

        var persistedLines = await context.SupplierInvoiceLines.AsNoTracking().ToListAsync();
        var savedAnchor = Assert.Single(persistedLines);
        Assert.Equal(anchorLine.Id, savedAnchor.Id);
        Assert.Equal(created.Id, savedAnchor.ResolvedProductId);
        Assert.Equal("Producto Nuevo Creado", savedAnchor.Name);
        Assert.Equal(SupplierInvoiceLineStatus.Update, savedAnchor.Status);

        var alias = await context.SupplierProductCodes.AsNoTracking().SingleAsync();
        Assert.Equal("CRE-1", alias.Code);
        Assert.Equal(created.Id, alias.ProductId);

        Assert.Equal(created.Id, Assert.Single(result.Lines).ResolvedProductId);

        // Los productos del vínculo previo quedan intactos.
        var savedPrevious = await context.Products.AsNoTracking().SingleAsync(product => product.Id == previousProduct.Id);
        Assert.Equal(6.00m, savedPrevious.CostPriceUSD);
        var savedExtra = await context.Products.AsNoTracking().SingleAsync(product => product.Id == extraProduct.Id);
        Assert.Equal(6.50m, savedExtra.CostPriceUSD);
    }

    [Fact]
    public async Task LinkProductToLineAsync_ResolvedGroup_ShrinksGroupDeletingReplacedRows()
    {
        var (service, context) = CreateService();
        var productA = new ProductBuilder()
            .WithId(0)
            .WithSku("7595000000017")
            .WithName("Grupo A")
            .WithCostAndMargin(2.00m, 30m)
            .WithStock(10m)
            .Build();
        var productB = new ProductBuilder()
            .WithId(0)
            .WithSku("7595000000024")
            .WithName("Grupo B")
            .WithCostAndMargin(2.50m, 20m)
            .WithStock(6m)
            .Build();
        context.Products.AddRange(productA, productB);
        await context.SaveChangesAsync();

        var anchorLine = CreateLine(
            supplierCode: "GRP-1",
            name: "GRUPO SURTIDO",
            quantity: 3m,
            unitCostUsd: 3m,
            status: SupplierInvoiceLineStatus.Update,
            resolvedProductId: productA.Id);
        var extraLine = CreateLine(
            supplierCode: "GRP-1",
            name: "Grupo B",
            quantity: 0m,
            unitCostUsd: 3m,
            status: SupplierInvoiceLineStatus.Update,
            resolvedProductId: productB.Id);
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, anchorLine, extraLine);

        var result = await service.LinkProductToLineAsync(
            invoice.Id,
            anchorLine.Id,
            new LinkInvoiceProductRequestDto(
                productA.Id,
                "GRUPO SURTIDO",
                new[] { productA.Id },
                ReplacedLineIds: new[] { extraLine.Id }));

        var resultLine = Assert.Single(result.Lines);
        Assert.Equal(anchorLine.Id, resultLine.Id);
        Assert.Equal(productA.Id, resultLine.ResolvedProductId);

        var persistedLines = await context.SupplierInvoiceLines.AsNoTracking().ToListAsync();
        Assert.Equal(anchorLine.Id, Assert.Single(persistedLines).Id);
    }

    [Fact]
    public async Task LinkProductToLineAsync_ResolvedGroup_ExpandsGroupReusingAndCreatingRows()
    {
        var (service, context) = CreateService();
        var productA = new ProductBuilder()
            .WithId(0)
            .WithSku("7597000000015")
            .WithName("Grupo Base")
            .WithCostAndMargin(1.00m, 30m)
            .WithStock(10m)
            .Build();
        var productC = new ProductBuilder()
            .WithId(0)
            .WithSku("7597000000022")
            .WithName("Grupo Reutilizado")
            .WithCostAndMargin(1.20m, 25m)
            .WithStock(8m)
            .Build();
        var productD = new ProductBuilder()
            .WithId(0)
            .WithSku("7597000000039")
            .WithName("Grupo Nuevo")
            .WithCostAndMargin(1.40m, 20m)
            .WithStock(5m)
            .Build();
        context.Products.AddRange(productA, productC, productD);
        await context.SaveChangesAsync();

        var anchorLine = CreateLine(
            supplierCode: "EXP-1",
            name: "GRUPO EXPANDIDO",
            quantity: 5m,
            unitCostUsd: 4m,
            status: SupplierInvoiceLineStatus.Update,
            resolvedProductId: productA.Id);
        var extraLine = CreateLine(
            supplierCode: "EXP-1",
            name: "Fila Extra Previa",
            quantity: 0m,
            unitCostUsd: 4m,
            status: SupplierInvoiceLineStatus.Update,
            resolvedProductId: productC.Id);
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, anchorLine, extraLine);

        var result = await service.LinkProductToLineAsync(
            invoice.Id,
            anchorLine.Id,
            new LinkInvoiceProductRequestDto(
                productA.Id,
                "GRUPO EXPANDIDO",
                new[] { productA.Id, productC.Id, productD.Id },
                ReplacedLineIds: new[] { extraLine.Id }));

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(productA.Id, result.Lines[0].ResolvedProductId);
        // Fila reutilizada: conserva su Id y se re-apunta al producto reutilizado.
        Assert.Equal(extraLine.Id, result.Lines[1].Id);
        Assert.Equal(productC.Id, result.Lines[1].ResolvedProductId);
        Assert.Equal(0m, result.Lines[1].Quantity);
        // Fila faltante: se crea con la plantilla del ancla.
        Assert.True(result.Lines[2].Id > 0);
        Assert.NotEqual(extraLine.Id, result.Lines[2].Id);
        Assert.Equal(productD.Id, result.Lines[2].ResolvedProductId);
        Assert.Equal(0m, result.Lines[2].Quantity);

        Assert.Equal(3, await context.SupplierInvoiceLines.AsNoTracking().CountAsync());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("quantity")]
    [InlineData("unresolved")]
    [InlineData("anchor")]
    public async Task LinkProductToLineAsync_ReplacedLineIds_Validation(string scenario)
    {
        var (service, context) = CreateService();
        var targetProduct = new ProductBuilder()
            .WithId(0)
            .WithSku("7594000000018")
            .WithName("Producto Destino")
            .WithCostAndMargin(1.00m, 30m)
            .WithStock(4m)
            .Build();
        var existingProduct = new ProductBuilder()
            .WithId(0)
            .WithSku("7594000000025")
            .WithName("Producto Existente")
            .WithCostAndMargin(1.50m, 25m)
            .WithStock(2m)
            .Build();
        context.Products.AddRange(targetProduct, existingProduct);
        await context.SaveChangesAsync();

        var anchorLine = CreateLine(
            supplierCode: "VAL-1",
            name: "LINEA ANCLA",
            quantity: 2m,
            unitCostUsd: 3m,
            status: SupplierInvoiceLineStatus.Update,
            resolvedProductId: existingProduct.Id);
        var replacedLine = CreateLine(
            supplierCode: "VAL-1",
            name: "LINEA EXTRA",
            quantity: scenario == "quantity" ? 1m : 0m,
            unitCostUsd: 3m,
            status: scenario == "unresolved" ? SupplierInvoiceLineStatus.New : SupplierInvoiceLineStatus.Update,
            resolvedProductId: scenario == "unresolved" ? null : existingProduct.Id);
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, anchorLine, replacedLine);
        const int foreignLineId = 987654;
        var replacedLineIds = scenario switch
        {
            "foreign" => new[] { foreignLineId },
            "anchor" => new[] { anchorLine.Id },
            _ => new[] { replacedLine.Id }
        };
        var expectedMessage = scenario switch
        {
            "foreign" => $"Supplier invoice line {foreignLineId} was not found in invoice {invoice.Id}.",
            "anchor" => $"Supplier invoice line {anchorLine.Id} cannot be replaced because it is the resolution anchor.",
            "quantity" => $"Supplier invoice line {replacedLine.Id} has a quantity and cannot be replaced.",
            _ => $"Supplier invoice line {replacedLine.Id} is not resolved and cannot be replaced."
        };

        if (scenario == "foreign")
        {
            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.LinkProductToLineAsync(
                invoice.Id,
                anchorLine.Id,
                new LinkInvoiceProductRequestDto(
                    targetProduct.Id,
                    ProductIds: new[] { targetProduct.Id },
                    ReplacedLineIds: replacedLineIds)));
            Assert.Equal(expectedMessage, exception.Message);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.LinkProductToLineAsync(
                invoice.Id,
                anchorLine.Id,
                new LinkInvoiceProductRequestDto(
                    targetProduct.Id,
                    ProductIds: new[] { targetProduct.Id },
                    ReplacedLineIds: replacedLineIds)));
            Assert.Equal(expectedMessage, exception.Message);
        }

        // Validación fallida = sin efectos: filas intactas y sin alias persistidos.
        Assert.Equal(2, await context.SupplierInvoiceLines.AsNoTracking().CountAsync());
        var savedAnchor = await context.SupplierInvoiceLines.AsNoTracking().SingleAsync(candidate => candidate.Id == anchorLine.Id);
        Assert.Equal(existingProduct.Id, savedAnchor.ResolvedProductId);
        Assert.Empty(await context.SupplierProductCodes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task LinkProductToLineAsync_ResolvedGroup_WithoutReplacedIds_LeavesExtraRowsResolved()
    {
        var (service, context) = CreateService();
        var productA = new ProductBuilder()
            .WithId(0)
            .WithSku("7598000000014")
            .WithName("Grupo Principal")
            .WithCostAndMargin(3.00m, 30m)
            .WithStock(10m)
            .Build();
        var productB = new ProductBuilder()
            .WithId(0)
            .WithSku("7598000000021")
            .WithName("Grupo Extra")
            .WithCostAndMargin(3.50m, 20m)
            .WithStock(6m)
            .Build();
        context.Products.AddRange(productA, productB);
        await context.SaveChangesAsync();

        var anchorLine = CreateLine(
            supplierCode: "NO-REPL-1",
            name: "GRUPO SIN REEMPLAZOS",
            quantity: 3m,
            unitCostUsd: 3m,
            status: SupplierInvoiceLineStatus.Update,
            resolvedProductId: productA.Id);
        var extraLine = CreateLine(
            supplierCode: "NO-REPL-1",
            name: "Fila Extra",
            quantity: 0m,
            unitCostUsd: 3m,
            status: SupplierInvoiceLineStatus.Update,
            resolvedProductId: productB.Id);
        var invoice = await AddInvoiceAsync(context, CurrencyCodes.Usd, 1m, anchorLine, extraLine);

        var result = await service.LinkProductToLineAsync(
            invoice.Id,
            anchorLine.Id,
            new LinkInvoiceProductRequestDto(productA.Id, "GRUPO SIN REEMPLAZOS"));

        // Sin ids explícitos el backend no adivina el grupo: la fila extra queda resuelta intacta.
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(productA.Id, result.Lines[0].ResolvedProductId);
        var persistedExtra = await context.SupplierInvoiceLines.AsNoTracking().SingleAsync(candidate => candidate.Id == extraLine.Id);
        Assert.Equal(productB.Id, persistedExtra.ResolvedProductId);
        Assert.Equal("Fila Extra", persistedExtra.Name);
        Assert.Null(persistedExtra.OldCostPriceUSD);
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
