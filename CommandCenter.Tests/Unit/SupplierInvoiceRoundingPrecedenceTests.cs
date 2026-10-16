using System;
using System.Collections.Generic;
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
/// 8.184-T4 (S7): precedencia de la regla de redondeo en el confirm de facturas de proveedor.
/// La regla efectiva de cada línea aprobada es <c>line.RoundingRule ?? request.RoundingRule</c>
/// (null en la línea = hereda la regla de la factura); una regla VES persistida en una línea
/// aprobada se rechaza con el mismo ArgumentException que la regla de factura, ANTES de abrir la
/// transacción (sin efectos), y ExactMath sigue byte-idéntico al flujo 8.182/8.183.
/// </summary>
public class SupplierInvoiceRoundingPrecedenceTests
{
    [Fact]
    public async Task ConfirmAsync_LineRuleTakesPrecedenceOverInvoiceRule()
    {
        // Línea con ExactMath persistido + factura Psicológico: la línea gana y los precios quedan
        // matemáticos (15.00 / 14.00), nunca 14.99 / 13.99.
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var product = CreateProduct("PRECEDENCE-LINE-WINS", 0m, 0m);
        var invoice = await AddInvoiceAsync(
            context,
            CreateLine(product, quantity: 1m, unitCost: 10m, roundingRule: RoundingRuleType.ExactMath));
        var line = Assert.Single(invoice.Lines);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(
            new[] { new ConfirmLineDto(line.Id, true, 50m, 40m) },
            RoundingRule: RoundingRuleType.UsdPsychological));

        var savedProduct = await context.Products.SingleAsync(candidate => candidate.Id == product.Id);
        Assert.Equal(15.00m, savedProduct.PriceRetailUSD);
        Assert.Equal(14.00m, savedProduct.PriceWholesaleUSD);
        Assert.Equal(50m, savedProduct.ProfitMarginRetail);
        Assert.Equal(40m, savedProduct.ProfitMarginWholesale);
    }

    [Fact]
    public async Task ConfirmAsync_LinePsychologicalRuleWinsOverExactInvoiceRule()
    {
        // Dirección inversa: línea Psicológico + factura ExactMath (default) → 14.99 / 13.99.
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var product = CreateProduct("PRECEDENCE-LINE-PSY", 0m, 0m);
        var invoice = await AddInvoiceAsync(
            context,
            CreateLine(product, quantity: 1m, unitCost: 10m, roundingRule: RoundingRuleType.UsdPsychological));
        var line = Assert.Single(invoice.Lines);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(
            new[] { new ConfirmLineDto(line.Id, true, 50m, 40m) }));

        var savedProduct = await context.Products.SingleAsync(candidate => candidate.Id == product.Id);
        Assert.Equal(14.99m, savedProduct.PriceRetailUSD);
        Assert.Equal(13.99m, savedProduct.PriceWholesaleUSD);
        Assert.Equal(50m, savedProduct.ProfitMarginRetail);
        Assert.Equal(40m, savedProduct.ProfitMarginWholesale);
    }

    [Fact]
    public async Task ConfirmAsync_LineWithoutRuleInheritsInvoiceRule()
    {
        // null en la línea = hereda la regla del confirm (regla de factura).
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var product = CreateProduct("PRECEDENCE-INHERIT", 0m, 0m);
        var invoice = await AddInvoiceAsync(
            context,
            CreateLine(product, quantity: 1m, unitCost: 10m, roundingRule: null));
        var line = Assert.Single(invoice.Lines);

        await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(
            new[] { new ConfirmLineDto(line.Id, true, 50m, 40m) },
            RoundingRule: RoundingRuleType.UsdPsychological));

        var savedProduct = await context.Products.SingleAsync(candidate => candidate.Id == product.Id);
        Assert.Equal(14.99m, savedProduct.PriceRetailUSD);
        Assert.Equal(13.99m, savedProduct.PriceWholesaleUSD);
        Assert.Equal(50m, savedProduct.ProfitMarginRetail);
        Assert.Equal(40m, savedProduct.ProfitMarginWholesale);
    }

    [Theory]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10)]
    public async Task ConfirmAsync_VesLineRuleOnApprovedLine_RejectsWithoutApplyingChanges(RoundingRuleType rule)
    {
        // La regla VES persistida en una línea aprobada se rechaza pre-transacción, con el mismo
        // contrato que la regla de factura y sin efectos (factura Draft, producto y línea intactos).
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var product = CreateProduct("PRECEDENCE-VES-LINE", 4m, 6m);
        var invoice = await AddInvoiceAsync(context, CreateLine(product, quantity: 2m, unitCost: 9m, roundingRule: rule));
        var line = Assert.Single(invoice.Lines);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(new[] { new ConfirmLineDto(line.Id, true, null, null) })));

        Assert.Equal("request", exception.ParamName);
        Assert.Contains("not supported", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"line {line.Id}", exception.Message, StringComparison.Ordinal);
        await AssertNoApplySideEffectsAsync(context, product, line);
    }

    [Fact]
    public async Task ConfirmAsync_VesLineRuleOnUnapprovedLine_DoesNotBlockApprovedLines()
    {
        // La validación cubre solo las líneas aprobadas del request: una fila con regla VES no
        // aprobada no bloquea el apply de las demás y no se aplica.
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(context);
        var approvedProduct = CreateProduct("PRECEDENCE-APPROVED", 4m, 6m);
        var rejectedProduct = CreateProduct("PRECEDENCE-REJECTED", 7m, 8m);
        var invoice = await AddInvoiceAsync(
            context,
            CreateLine(approvedProduct, quantity: 1m, unitCost: 10m, roundingRule: RoundingRuleType.ExactMath),
            CreateLine(rejectedProduct, quantity: 1m, unitCost: 12m, roundingRule: RoundingRuleType.VesCashFriendlyStep5));
        var approvedLine = invoice.Lines.OrderBy(line => line.Id).First();
        var rejectedLine = invoice.Lines.OrderBy(line => line.Id).Last();

        var result = await service.ConfirmAsync(invoice.Id, new ConfirmSupplierInvoiceRequestDto(new[]
        {
            new ConfirmLineDto(approvedLine.Id, true, 50m, 40m),
            new ConfirmLineDto(rejectedLine.Id, false, null, null)
        }));

        Assert.Equal(nameof(SupplierInvoiceStatus.Applied), result.Status);
        var savedApproved = await context.Products.SingleAsync(candidate => candidate.Id == approvedProduct.Id);
        var savedRejected = await context.Products.SingleAsync(candidate => candidate.Id == rejectedProduct.Id);
        Assert.Equal(15.00m, savedApproved.PriceRetailUSD);
        Assert.Equal(7m, savedRejected.CostPriceUSD);
        Assert.Equal(8m, savedRejected.StockQuantity);
        var savedRejectedLine = await context.SupplierInvoiceLines.SingleAsync(candidate => candidate.Id == rejectedLine.Id);
        Assert.False(savedRejectedLine.IsApproved);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, savedRejectedLine.RoundingRule);
    }

    private static async Task AssertNoApplySideEffectsAsync(
        InventoryDbContext context,
        Product product,
        SupplierInvoiceLine line)
    {
        var savedProduct = await context.Products.SingleAsync(candidate => candidate.Id == product.Id);
        var savedLine = await context.SupplierInvoiceLines.SingleAsync(candidate => candidate.Id == line.Id);
        var savedInvoice = await context.SupplierInvoices.SingleAsync(candidate => candidate.Id == line.SupplierInvoiceId);
        Assert.Equal(4m, savedProduct.CostPriceUSD);
        Assert.Equal(6m, savedProduct.StockQuantity);
        Assert.Equal(20m, savedProduct.PriceRetailUSD);
        Assert.Equal(2m, savedLine.Quantity);
        Assert.Equal(9m, savedLine.UnitCostUSD);
        Assert.Equal(SupplierInvoiceStatus.Draft, savedInvoice.Status);
        Assert.Null(savedLine.SuggestedRetailPriceUSD);
        Assert.Empty(await context.StockMovements.ToListAsync());
    }

    private static SupplierInvoiceService CreateService(InventoryDbContext context) =>
        new(
            context,
            Mock.Of<ISystemSettingsService>(),
            Mock.Of<ISupplierProductSimilaritySearch>(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IProductManagementService>());

    private static async Task<SupplierInvoice> AddInvoiceAsync(InventoryDbContext context, params SupplierInvoiceLine[] lines)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var invoice = new SupplierInvoice
        {
            Supplier = new Supplier
            {
                CommercialName = $"Rounding Precedence Supplier {suffix}",
                NormalizedCommercialName = $"ROUNDING PRECEDENCE SUPPLIER {suffix}"
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

    private static Product CreateProduct(string sku, decimal cost, decimal stock) => new()
    {
        Name = sku,
        SKU = sku,
        CostPriceUSD = cost,
        Cost = 91m,
        ProfitMarginRetail = 50m,
        ProfitMarginWholesale = 40m,
        ProfitPercentage = 50m,
        HasWholesale = true,
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
        decimal unitCost,
        RoundingRuleType? roundingRule) => new()
    {
        Quantity = quantity,
        UnitCostUSD = unitCost,
        Status = SupplierInvoiceLineStatus.Update,
        ResolvedProduct = product,
        MatchMethod = MatchMethod.Barcode,
        RoundingRule = roundingRule
    };
}
