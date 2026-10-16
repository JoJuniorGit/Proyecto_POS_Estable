using System;
using System.Collections.Generic;
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
/// 8.185-T4 (S1/S2/S7): las sugerencias del POS derivan el Bs.S efectivo con la regla RESUELTA
/// del quick-info (?? política global del cliente) y el toggle vigente. Los refrescos llegan por
/// mensajes en memoria: políticas globales re-ejecutan la búsqueda activa (patrón 8.143) y el
/// override de un artículo se recomputa en memoria sin consultas.
/// </summary>
public class PosSearchSuggestionsRoundingTests
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

    private static (PosViewModel Vm, Mock<IProductService> Products) CreateVm(
        ProductQuickInfoDto suggestion,
        decimal rate,
        PricingPoliciesDto? policies = null)
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.Setup(r => r.CurrentRate).Returns(rate);
        mockProducts
            .Setup(p => p.GetSuggestionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProductQuickInfoDto> { suggestion });

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        // Aísla el carrito del bus global compartido entre pruebas (CurrentSaleChangedMessage).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        ISettingsService? settingsService = null;
        if (policies != null)
        {
            var settings = new Mock<ISettingsService>();
            settings.Setup(s => s.GetPricingPoliciesAsync()).ReturnsAsync(policies);
            settingsService = settings.Object;
        }

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            new UserSession(),
            dialogService: null,
            dispatcherInvoker: null,
            recoveryStore: null,
            settingsService: settingsService);

        return (vm, mockProducts);
    }

    [Fact]
    public async Task PosSearchSuggestions_ResolvedCashRule_WinsOverGlobalPolicy()
    {
        var suggestion = new ProductQuickInfoDto
        {
            Id = 42,
            Name = "Harina PAN",
            SKU = "HAR-42",
            PriceUSD = 12.00m,
            PriceBsS = 1.00m, // snapshot viejo deliberado
            RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep5,
        };
        var (vm, _) = CreateVm(suggestion, 36.50m, new PricingPoliciesDto
        {
            CashBsRoundingRule = RoundingRuleType.ExactMath,
            EnforceMinimumMarginFloor = true,
        });

        try
        {
            vm.SearchText = "Harina";

            bool derived = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS == 440.00m);
            Assert.True(derived, $"La sugerencia no usó la regla resuelta del DTO. Actual: {(vm.Suggestions.Count > 0 ? vm.Suggestions[0].PriceBsS : -1m)}");
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task PosSearchSuggestions_NullResolvedRule_FallsBackToClientGlobalPolicy()
    {
        var suggestion = new ProductQuickInfoDto
        {
            Id = 42,
            Name = "Harina PAN",
            SKU = "HAR-42",
            PriceUSD = 12.00m,
            PriceBsS = 1.00m, // snapshot viejo deliberado
        };
        var (vm, _) = CreateVm(suggestion, 36.50m, new PricingPoliciesDto
        {
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5,
            EnforceMinimumMarginFloor = true,
        });

        try
        {
            vm.SearchText = "Harina";

            bool derived = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS == 440.00m);
            Assert.True(derived, $"La sugerencia no usó la política global del cliente. Actual: {(vm.Suggestions.Count > 0 ? vm.Suggestions[0].PriceBsS : -1m)}");
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task PosSearchSuggestions_ToggleOff_KeepsNearestStep()
    {
        var suggestion = new ProductQuickInfoDto
        {
            Id = 42,
            Name = "Harina PAN",
            SKU = "HAR-42",
            PriceUSD = 11.89m,
            RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep10,
        };
        var (vm, _) = CreateVm(suggestion, 36.50m, new PricingPoliciesDto
        {
            CashBsRoundingRule = RoundingRuleType.ExactMath,
            EnforceMinimumMarginFloor = false,
        });

        try
        {
            vm.SearchText = "Harina";

            // 11.89 × 36.50 = 433.985 → múltiplo de 10 más cercano 430 (toggle OFF ⇒ puede bajar).
            bool derived = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS == 430.00m);
            Assert.True(derived, $"La sugerencia no usó el escalón más cercano con el toggle OFF. Actual: {(vm.Suggestions.Count > 0 ? vm.Suggestions[0].PriceBsS : -1m)}");
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task PosSearchSuggestions_PoliciesChangedMessage_ReExecutesActiveSearch()
    {
        var suggestion = new ProductQuickInfoDto
        {
            Id = 42,
            Name = "Harina PAN",
            SKU = "HAR-42",
            PriceUSD = 12.00m,
            PriceBsS = 1.00m,
        };
        var (vm, _) = CreateVm(suggestion, 36.50m, new PricingPoliciesDto());

        try
        {
            vm.SearchText = "Harina";

            bool initial = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS == 438.00m);
            Assert.True(initial, "La búsqueda inicial no derivó el techo canónico.");

            WeakReferenceMessenger.Default.Send(new PricingPoliciesChangedMessage(new PricingPoliciesDto
            {
                CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5,
                EnforceMinimumMarginFloor = true,
            }));

            bool refreshed = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS == 440.00m);
            Assert.True(refreshed, $"La búsqueda activa no se re-ejecutó con las políticas nuevas. Actual: {(vm.Suggestions.Count > 0 ? vm.Suggestions[0].PriceBsS : -1m)}");
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task PosSearchSuggestions_ProductRoundingChangedMessage_PatchesItemInMemory()
    {
        var suggestion = new ProductQuickInfoDto
        {
            Id = 42,
            Name = "Harina PAN",
            SKU = "HAR-42",
            PriceUSD = 12.00m,
            PriceBsS = 1.00m,
        };
        var (vm, products) = CreateVm(suggestion, 36.50m, new PricingPoliciesDto());

        try
        {
            vm.SearchText = "Harina";

            bool initial = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS == 438.00m);
            Assert.True(initial, "La búsqueda inicial no derivó el techo canónico.");

            WeakReferenceMessenger.Default.Send(new ProductRoundingChangedMessage(42, RoundingRuleType.VesCashFriendlyStep5));

            Assert.Equal(440.00m, vm.Suggestions[0].PriceBsS);
            products.Verify(
                p => p.GetSuggestionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
