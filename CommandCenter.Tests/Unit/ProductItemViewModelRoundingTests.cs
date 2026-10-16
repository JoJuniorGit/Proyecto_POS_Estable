using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.185-T4 (S1/S2/S7): el Bs.S mostrado del Catálogo sale del helper único del cliente
/// (override del artículo ?? política global + toggle de piso) y se refresca por mensajes en
/// memoria — 0 consultas por fila.
/// </summary>
public class ProductItemViewModelRoundingTests
{
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(25);
        }

        return condition();
    }

    private static (ProductItemViewModel Item, Mock<IExchangeRateService> Rate) CreateItem(
        decimal priceUsd,
        RoundingRuleType? cashRule,
        decimal rate = 36.50m,
        decimal wholesaleUsd = 0m)
    {
        var dto = new ProductDto
        {
            Id = 7,
            Name = "Harina P.A.N.",
            SKU = "H-1",
            PriceUSD = priceUsd,
            PriceRetailUSD = priceUsd,
            PriceWholesaleUSD = wholesaleUsd,
            HasWholesale = wholesaleUsd > 0m,
            RoundingRuleCashBs = cashRule,
        };

        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(r => r.CurrentRate).Returns(rate);
        return (new ProductItemViewModel(dto, rateService.Object), rateService);
    }

    [Fact]
    public void DisplayRetailPrice_WithoutCashRule_KeepsCanonicalCeiling()
    {
        var (item, _) = CreateItem(12m, cashRule: null);

        Assert.Equal($"Bs.S {CurrencyDisplay.Number(438m)}", item.DisplayRetailPrice);
    }

    [Fact]
    public void DisplayRetailPrice_WithArticleCashRule_UsesNeverDownEffectiveBsS()
    {
        var (item, _) = CreateItem(12m, RoundingRuleType.VesCashFriendlyStep5);

        // 12.00 × 36.50 = 438.00 → múltiplo de 5 más cercano 440 (ya ≥ matemático).
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(440m)}", item.DisplayRetailPrice);
    }

    [Fact]
    public void DisplayRetailPrice_ToggleOff_RoundsToNearestLowerStep()
    {
        var (item, _) = CreateItem(11.89m, RoundingRuleType.VesCashFriendlyStep10);

        item.ApplyPricingPolicies(globalCashRoundingRule: null, neverRoundDownCashBs: false);

        // 11.89 × 36.50 = 433.985 → múltiplo de 10 más cercano 430 (por debajo).
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(430m)}", item.DisplayRetailPrice);
    }

    [Fact]
    public void DisplayRetailPrice_ToggleOn_NeverRoundsDown()
    {
        var (item, _) = CreateItem(11.89m, RoundingRuleType.VesCashFriendlyStep10);

        item.ApplyPricingPolicies(globalCashRoundingRule: null, neverRoundDownCashBs: true);

        Assert.Equal($"Bs.S {CurrencyDisplay.Number(440m)}", item.DisplayRetailPrice);
    }

    [Fact]
    public void DisplayRetailPrice_GlobalCashRule_AppliesWhenArticleHasNoOverride()
    {
        var (item, _) = CreateItem(12m, cashRule: null);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(438m)}", item.DisplayRetailPrice);

        item.ApplyPricingPolicies(RoundingRuleType.VesCashFriendlyStep5, neverRoundDownCashBs: true);

        Assert.Equal($"Bs.S {CurrencyDisplay.Number(440m)}", item.DisplayRetailPrice);
    }

    [Fact]
    public void DisplayWholesalePrice_WithCashRule_UsesSameEffectiveHelper()
    {
        var (item, _) = CreateItem(13m, RoundingRuleType.VesCashFriendlyStep5, wholesaleUsd: 12m);

        Assert.Equal($"Bs.S {CurrencyDisplay.Number(440m)}", item.DisplayWholesalePrice);
        Assert.Equal(440m, item.EffectivePriceWholesaleBsS);
    }

    [Fact]
    public void UpdateExchangeRate_WithCashRule_RecomputesEffectiveBsS()
    {
        decimal currentRate = 36.50m;
        var dto = new ProductDto
        {
            Id = 7,
            Name = "Harina P.A.N.",
            SKU = "H-1",
            PriceUSD = 12m,
            PriceRetailUSD = 12m,
            RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep5,
        };
        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(r => r.CurrentRate).Returns(() => currentRate);
        var item = new ProductItemViewModel(dto, rateService.Object);

        Assert.Equal($"Bs.S {CurrencyDisplay.Number(440m)}", item.DisplayRetailPrice);

        currentRate = 50.00m;
        item.UpdateExchangeRate();

        Assert.Equal(600m, item.PriceBsS);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(600m)}", item.DisplayRetailPrice);
    }

    [Fact]
    public async Task InventoryViewModel_InitialPolicyFetch_AppliesGlobalRuleToRows()
    {
        var productService = new Mock<IProductService>();
        productService
            .Setup(p => p.GetPagedAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>()))
            .ReturnsAsync(new PagedResultDto<ProductDto>
            {
                Items = new List<ProductDto>
                {
                    new ProductDto { Id = 7, Name = "Harina P.A.N.", SKU = "H-1", PriceUSD = 12m, PriceRetailUSD = 12m, IsActive = true },
                },
                TotalCount = 1,
            });

        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(r => r.CurrentRate).Returns(36.50m);
        rateService.Setup(r => r.GetCurrentRateAsync()).ReturnsAsync((36.50m, DateTime.UtcNow));

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetPricingPoliciesAsync()).ReturnsAsync(new PricingPoliciesDto
        {
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5,
            EnforceMinimumMarginFloor = true,
        });

        using var vm = new InventoryViewModel(
            productService.Object,
            rateService.Object,
            userSession: null,
            settingsService: settings.Object);

        bool loaded = await WaitUntilAsync(() => vm.Products.Count == 1);
        Assert.True(loaded, "El catálogo no cargó la página inicial.");

        await vm.PricingPoliciesLoadTask;

        Assert.Equal($"Bs.S {CurrencyDisplay.Number(440m)}", vm.Products[0].DisplayRetailPrice);
    }

    [Fact]
    public void InventoryViewModel_PoliciesChangedMessage_AppliesInMemoryWithoutQueries()
    {
        var productService = new Mock<IProductService>();
        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(r => r.CurrentRate).Returns(36.50m);

        using var vm = new InventoryViewModel(productService.Object, rateService.Object, userSession: new UserSession());
        vm.Products = new ObservableCollection<ProductItemViewModel>
        {
            new ProductItemViewModel(
                new ProductDto { Id = 7, Name = "Harina P.A.N.", SKU = "H-1", PriceUSD = 12m, PriceRetailUSD = 12m },
                rateService.Object),
        };

        WeakReferenceMessenger.Default.Send(new PricingPoliciesChangedMessage(new PricingPoliciesDto
        {
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5,
            EnforceMinimumMarginFloor = true,
        }));

        Assert.Equal($"Bs.S {CurrencyDisplay.Number(440m)}", vm.Products[0].DisplayRetailPrice);
        productService.Verify(
            p => p.GetPagedAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>()),
            Times.Never);
    }
}
