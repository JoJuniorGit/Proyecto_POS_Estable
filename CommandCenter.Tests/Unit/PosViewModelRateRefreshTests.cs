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
/// 8.143: el buscador del POS debe derivar PriceBsS de USD x tasa vigente (ignorando el
/// snapshot viejo del DTO) y regenerar sugerencias al recibir ExchangeRateChangedMessage,
/// porque los DTO de sugerencias no son observables.
/// </summary>
public class PosViewModelRateRefreshTests
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

    private static (PosViewModel Vm, Mock<IExchangeRateService> Rate) CreateVm(ProductQuickInfoDto suggestion, decimal initialRate)
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.Setup(r => r.CurrentRate).Returns(initialRate);
        mockProducts.Setup(p => p.GetSuggestionsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<ProductQuickInfoDto> { suggestion });

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        // Aisla el carrito del bus global compartido entre pruebas (CurrentSaleChangedMessage).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            new UserSession());

        return (vm, mockRate);
    }

    [Fact]
    public async Task ExecuteSearch_DerivaBsSDeTasa_IgnoraSnapshot()
    {
        var suggestion = new ProductQuickInfoDto
        {
            Id = 42,
            Name = "Harina PAN",
            SKU = "HAR-42",
            PriceUSD = 10.00m,
            PriceBsS = 100.00m // Snapshot viejo calculado con tasa 10
        };
        var (vm, _) = CreateVm(suggestion, 50.00m);

        try
        {
            vm.SearchText = "Harina";

            bool derived = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS == 500.00m);
            Assert.True(derived, $"La busqueda no derivo el precio con la tasa vigente. Actual: {(vm.Suggestions.Count > 0 ? vm.Suggestions[0].PriceBsS : -1m)}");
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task ExchangeRateChangedMessage_ConBusquedaActiva_RegeneraSugerencias()
    {
        var suggestion = new ProductQuickInfoDto
        {
            Id = 42,
            Name = "Harina PAN",
            SKU = "HAR-42",
            PriceUSD = 10.00m,
            PriceBsS = 100.00m // Snapshot viejo calculado con tasa 10
        };
        var (vm, mockRate) = CreateVm(suggestion, 50.00m);

        try
        {
            vm.SearchText = "Harina";

            bool initialDerived = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS == 500.00m);
            Assert.True(initialDerived, "La busqueda inicial no derivo el precio con la tasa vigente.");

            mockRate.Setup(r => r.CurrentRate).Returns(60.00m);
            WeakReferenceMessenger.Default.Send(new ExchangeRateChangedMessage(60.00m));

            bool refreshed = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS == 600.00m);
            Assert.True(refreshed, $"Las sugerencias no se regeneraron con la tasa nueva. Actual: {(vm.Suggestions.Count > 0 ? vm.Suggestions[0].PriceBsS : -1m)}");
            Assert.Equal(60.00m, vm.CurrentExchangeRate);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task ExecuteSearch_UsesRetailChannel_WhenLegacyPriceUsdIsStale()
    {
        // Tras una edición masiva el canal Detal es la autoridad; PriceUSD puede quedar viejo.
        var suggestion = new ProductQuickInfoDto
        {
            Id = 43,
            Name = "Cemento",
            SKU = "CEM-43",
            PriceUSD = 10.00m,
            PriceRetailUSD = 12.00m,
            PriceBsS = 100.00m
        };
        var (vm, _) = CreateVm(suggestion, 50.00m);

        try
        {
            vm.SearchText = "Cemento";

            bool derived = await WaitUntilAsync(() => vm.Suggestions.Count == 1 && vm.Suggestions[0].PriceBsS == 600.00m);
            Assert.True(derived, $"El POS no uso el canal Detal. Actual: {(vm.Suggestions.Count > 0 ? vm.Suggestions[0].PriceBsS : -1m)}");
            Assert.Equal(600.00m, vm.Suggestions[0].EffectivePriceBsS);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
