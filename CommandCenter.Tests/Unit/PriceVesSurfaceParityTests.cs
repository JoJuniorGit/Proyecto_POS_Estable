using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.185-T5 (S1/S4/S5): paridad tri-superficie del precio efectivo unitario en Bs.S
/// (Catálogo == POS == Carrito) para las cuatro reglas, y semántica viva del carrito:
/// la regla cash por línea es el snapshot resuelto por el servidor al agregar, el toggle
/// (nunca-hacia-abajo) se refresca por políticas y la tasa se recalcula en memoria.
/// </summary>
public class PriceVesSurfaceParityTests
{
    private const decimal Rate = 36.50m;

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(25);
        }

        return condition();
    }

    // ---------------------------------------------------------------------------------------------
    // Superficies.
    // ---------------------------------------------------------------------------------------------

    private static decimal ResolveCatalogBsS(RoundingRuleType? productRule, decimal priceUsd, PricingPoliciesDto? policies = null)
    {
        var rateMock = new Mock<IExchangeRateService>();
        rateMock.Setup(r => r.CurrentRate).Returns(Rate);

        var product = new ProductItemViewModel(
            new ProductDto
            {
                Id = 1,
                Name = "Parity Product",
                SKU = "PAR-1",
                IsActive = true,
                PriceUSD = priceUsd,
                PriceBsS = 0m,
                RoundingRuleCashBs = productRule
            },
            rateMock.Object);

        var effectivePolicies = policies ?? new PricingPoliciesDto();
        product.ApplyPricingPolicies(effectivePolicies.CashBsRoundingRule, effectivePolicies.EnforceMinimumMarginFloor);
        product.UpdateExchangeRate();
        return product.PriceBsS;
    }

    private static async Task<decimal> ResolvePosSuggestionBsSAsync(RoundingRuleType? resolvedRule, decimal priceUsd, PricingPoliciesDto policies)
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.Setup(r => r.CurrentRate).Returns(Rate);
        var mockDialog = new Mock<IDialogService>();

        mockProducts
            .Setup(p => p.GetSuggestionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProductQuickInfoDto>
            {
                new()
                {
                    Id = 42,
                    Name = "Parity Product",
                    SKU = "PAR-42",
                    PriceUSD = priceUsd,
                    PriceBsS = 1m, // snapshot viejo deliberado: la sugerencia debe re-derivar
                    RoundingRuleCashBs = resolvedRule
                }
            });

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetPricingPoliciesAsync()).ReturnsAsync(policies);

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            new UserSession(),
            mockDialog.Object,
            dispatcherInvoker: null,
            recoveryStore: null,
            settingsService: settings.Object);

        try
        {
            vm.SearchText = "Parity";
            bool derived = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS > 1m);
            Assert.True(derived, $"La sugerencia del POS no re-derivó el Bs.S efectivo. Actual: {(vm.Suggestions.Count > 0 ? vm.Suggestions[0].PriceBsS : -1m)}");
            return vm.Suggestions[0].PriceBsS;
        }
        finally
        {
            vm.Dispose();
        }
    }

    private static CartViewModel CreateCart(SaleDto sale, bool keepBusRegistration = false)
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.SetupGet(r => r.CurrentRate).Returns(Rate);
        var cart = new CartViewModel(mockSales.Object, mockRate.Object);
        if (!keepBusRegistration)
        {
            // Aísla el carrito del bus global compartido entre pruebas (patrón de la suite).
            WeakReferenceMessenger.Default.UnregisterAll(cart);
        }

        cart.CurrentSale = sale;
        return cart;
    }

    private static SaleDto PendingSale(params SaleItemDto[] items)
    {
        var sale = new SaleDto
        {
            Id = 101,
            Status = "Pending",
            AppliedRate = Rate
        };
        sale.Items.AddRange(items);
        sale.Subtotal = 0m;
        sale.TotalUSD = 0m;
        return sale;
    }

    private static SaleItemDto Line(decimal unitPrice, decimal quantity, RoundingRuleType? rule)
        => new()
        {
            Id = 1,
            ProductId = 1,
            ProductName = "Parity Product",
            Quantity = quantity,
            UnitPrice = unitPrice,
            Subtotal = unitPrice * quantity,
            RoundingRuleCashBs = rule
        };

    // ---------------------------------------------------------------------------------------------
    // Paridad tri-superficie.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RoundingRuleType.ExactMath, 12.23, 446.40)]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, 12.23, 447)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, 12.23, 450)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, 12.23, 450)]
    public async Task TriSurface_SameInputs_SameEffectiveUnitPriceBsS(RoundingRuleType rule, double usd, double expected)
    {
        decimal priceUsd = (decimal)usd;
        decimal expectedBsS = (decimal)expected;

        var cart = CreateCart(PendingSale(Line(priceUsd, 1m, rule)));

        decimal catalogBsS = ResolveCatalogBsS(rule, priceUsd);
        decimal posBsS = await ResolvePosSuggestionBsSAsync(rule, priceUsd, new PricingPoliciesDto());
        decimal cartLineBsS = cart.CartItems[0].UnitPriceBsS;

        Assert.Equal(expectedBsS, catalogBsS);
        Assert.Equal(expectedBsS, posBsS);
        Assert.Equal(expectedBsS, cartLineBsS);

        // Las tres superficies leen el mismo punto de cálculo: un único monto efectivo.
        Assert.Equal(catalogBsS, posBsS);
        Assert.Equal(catalogBsS, cartLineBsS);
    }

    [Fact]
    public async Task CatalogAndCart_GlobalPolicyInherited_ThroughResolvedSnapshot_AgreeWithPos()
    {
        // Sin override por producto: Catálogo/POS heredan la política global y el carrito recibe
        // del servidor la regla RESUELTA (mismo escalón). 12.00 × 36.50 = 438 → escalón ×5: 440.
        var policies = new PricingPoliciesDto
        {
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5,
            EnforceMinimumMarginFloor = true
        };

        var cart = CreateCart(PendingSale(Line(12.00m, 1m, RoundingRuleType.VesCashFriendlyStep5)));

        decimal catalogBsS = ResolveCatalogBsS(productRule: null, 12.00m, policies);
        decimal posBsS = await ResolvePosSuggestionBsSAsync(resolvedRule: null, 12.00m, policies);
        decimal cartLineBsS = cart.CartItems[0].UnitPriceBsS;

        Assert.Equal(440m, catalogBsS);
        Assert.Equal(440m, posBsS);
        Assert.Equal(440m, cartLineBsS);
    }

    [Fact]
    public async Task CatalogAndCart_ProductOverride_WinsOverGlobalPolicy_AgreeWithPos()
    {
        // Override del artículo ×5 pisa la política global ×10 en las tres superficies.
        // 11.89 × 36.50 = 433.985 → ×5: 435 (nunca-hacia-abajo); ×10 puro daría 440.
        var globalPolicies = new PricingPoliciesDto
        {
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep10,
            EnforceMinimumMarginFloor = true
        };

        var cart = CreateCart(PendingSale(Line(11.89m, 1m, RoundingRuleType.VesCashFriendlyStep5)));

        decimal catalogBsS = ResolveCatalogBsS(RoundingRuleType.VesCashFriendlyStep5, 11.89m, globalPolicies);
        decimal posBsS = await ResolvePosSuggestionBsSAsync(RoundingRuleType.VesCashFriendlyStep5, 11.89m, globalPolicies);
        decimal cartLineBsS = cart.CartItems[0].UnitPriceBsS;

        Assert.Equal(435m, catalogBsS);
        Assert.Equal(435m, posBsS);
        Assert.Equal(435m, cartLineBsS);
    }

    // ---------------------------------------------------------------------------------------------
    // Semántica viva del carrito.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cart_WholesaleSwitch_RecomputesLiveEffectiveUnitPriceBsS()
    {
        var retail = PendingSale(Line(12.00m, 1m, RoundingRuleType.VesCashFriendlyStep5));
        var wholesaleSale = PendingSale(Line(10.00m, 1m, RoundingRuleType.VesCashFriendlyStep5));
        wholesaleSale.PriceListType = "Wholesale";
        wholesaleSale.Items[0].IsWholesaleApplied = true;

        var mockSales = new Mock<ISalesService>();
        mockSales.Setup(s => s.UpdatePriceListAsync(101, "Wholesale")).ReturnsAsync(wholesaleSale);
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.SetupGet(r => r.CurrentRate).Returns(Rate);

        var cart = new CartViewModel(mockSales.Object, mockRate.Object);
        WeakReferenceMessenger.Default.UnregisterAll(cart);
        cart.CurrentSale = retail;

        Assert.False(cart.CartItems[0].IsWholesaleApplied);
        Assert.Equal(440m, cart.CartItems[0].UnitPriceBsS);

        await cart.SetPriceListCommand.ExecuteAsync("Wholesale");

        Assert.True(cart.CartItems[0].IsWholesaleApplied);
        Assert.Equal(365m, cart.CartItems[0].UnitPriceBsS); // 10.00 × 36.50 exacto en el escalón ×5
        Assert.Equal(365m, cart.TotalAmountLocal);
    }

    [Fact]
    public void Cart_FractionalQuantity_SubtotalKeepsDigitalCents_OnEffectiveUnitPrice()
    {
        // Pesable: 0.450 × 450 (escalón ×5 del efectivo) = 202.50 → RoundToDigital conserva centavos.
        var line = Line(12.23m, 0.450m, RoundingRuleType.VesCashFriendlyStep5);
        line.IsFractional = true;
        line.UnitOfMeasure = UnitOfMeasureType.Kg;

        var cart = CreateCart(PendingSale(line));

        Assert.Equal(450m, cart.CartItems[0].UnitPriceBsS);
        Assert.Equal(202.50m, cart.CartItems[0].SubtotalBsS);
        Assert.Equal(202.50m, cart.TotalAmountLocal);
    }

    [Fact]
    public void Cart_ExchangeRateChanged_RecomputesEffectiveUnitPriceBsS_InMemory()
    {
        var cart = CreateCart(PendingSale(Line(12.00m, 1m, RoundingRuleType.VesCashFriendlyStep5)), keepBusRegistration: true);

        Assert.Equal(440m, cart.CartItems[0].UnitPriceBsS);

        WeakReferenceMessenger.Default.Send(new ExchangeRateChangedMessage(20m));

        Assert.Equal(240m, cart.CartItems[0].UnitPriceBsS); // 12.00 × 20 = 240 (escalón exacto)
        Assert.Equal(240m, cart.TotalAmountLocal);
    }

    [Fact]
    public void Cart_PoliciesChanged_ToggleOff_UsesNearestStep()
    {
        // 11.89 × 36.50 = 433.985 → ×10 más cercano 430 (puede bajar con el toggle OFF).
        var cart = CreateCart(PendingSale(Line(11.89m, 1m, RoundingRuleType.VesCashFriendlyStep10)), keepBusRegistration: true);

        Assert.Equal(440m, cart.CartItems[0].UnitPriceBsS);

        WeakReferenceMessenger.Default.Send(new PricingPoliciesChangedMessage(new PricingPoliciesDto
        {
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep10,
            EnforceMinimumMarginFloor = false
        }));

        Assert.Equal(430m, cart.CartItems[0].UnitPriceBsS);
    }

    [Fact]
    public void Cart_PoliciesChanged_DoesNotResolveLegacyNullSnapshotLines()
    {
        // Líneas previas a 8.185 (regla null) conservan el techo canónico 8.104: misma semántica
        // del servidor (inmutabilidad del snapshot), las políticas globales no las reescriben.
        var cart = CreateCart(PendingSale(Line(12.23m, 1m, null)), keepBusRegistration: true);

        Assert.Equal(446.40m, cart.CartItems[0].UnitPriceBsS);

        WeakReferenceMessenger.Default.Send(new PricingPoliciesChangedMessage(new PricingPoliciesDto
        {
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5,
            EnforceMinimumMarginFloor = true
        }));

        Assert.Equal(446.40m, cart.CartItems[0].UnitPriceBsS);
    }

    [Fact]
    public async Task Cart_InitialPolicyFetch_AppliesToggleToLiveLines()
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.SetupGet(r => r.CurrentRate).Returns(Rate);
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetPricingPoliciesAsync()).ReturnsAsync(new PricingPoliciesDto
        {
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep10,
            EnforceMinimumMarginFloor = false
        });

        var cart = new CartViewModel(mockSales.Object, mockRate.Object, settingsService: settings.Object);
        WeakReferenceMessenger.Default.UnregisterAll(cart);
        cart.CurrentSale = PendingSale(Line(11.89m, 1m, RoundingRuleType.VesCashFriendlyStep10));

        // 11.89 × 36.50 = 433.985 → ×10 más cercano 430 con el toggle OFF leído al iniciar.
        bool applied = await WaitUntilAsync(() => cart.CartItems[0].UnitPriceBsS == 430m);
        Assert.True(applied, $"El toggle inicial de Ajustes no se aplicó al carrito. Actual: {cart.CartItems[0].UnitPriceBsS}");
        settings.Verify(s => s.GetPricingPoliciesAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public void Cart_TotalAmountLocal_SumsEffectiveSubtotals()
    {
        // El total a cobrar parte de los subtotales efectivos del carrito:
        // 12.23 ×2 → 450 ×2 = 900; 12.00 ×1 → 440; total 1340.
        var sale = PendingSale(
            Line(12.23m, 2m, RoundingRuleType.VesCashFriendlyStep5),
            new SaleItemDto
            {
                Id = 2,
                ProductId = 2,
                ProductName = "Parity Product 2",
                Quantity = 1m,
                UnitPrice = 12.00m,
                Subtotal = 12.00m,
                RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep10
            });

        var cart = CreateCart(sale);

        Assert.Equal(900m, cart.CartItems[0].SubtotalBsS);
        Assert.Equal(440m, cart.CartItems[1].SubtotalBsS);
        Assert.Equal(1340m, cart.SubtotalLocal);
        Assert.Equal(1340m, cart.TotalAmountLocal);
    }
}
