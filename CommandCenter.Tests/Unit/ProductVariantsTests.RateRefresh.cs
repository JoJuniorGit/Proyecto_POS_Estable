using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Helpers;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.143: el catalogo y el popup de variantes deben derivar todo precio Bs.S mostrado
/// de USD x tasa vigente (ceiling 2 decimales). El snapshot PriceBsS del DTO (fijado
/// al alta) solo es fallback cuando no hay USD o la tasa no es valida.
/// </summary>
public partial class ProductVariantsTests
{
    private static Mock<IExchangeRateService> CreateRateMock(decimal rate)
    {
        var mock = new Mock<IExchangeRateService>();
        mock.Setup(e => e.CurrentRate).Returns(rate);
        return mock;
    }

    [Fact]
    public void ProductItemViewModel_DisplayRetailPrice_DerivaDeTasa_IgnoraSnapshotViejo()
    {
        var rateMock = CreateRateMock(50.00m);
        var dto = new ProductDto
        {
            Id = 900,
            Name = "Harina PAN",
            SKU = "HAR-900",
            IsActive = true,
            PriceUSD = 10.00m,
            PriceBsS = 100.00m // Snapshot viejo calculado con tasa 10
        };
        var vm = new ProductItemViewModel(dto, rateMock.Object);

        var previous = CurrencyDisplay.Current;
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
        try
        {
            Assert.Equal("Bs.S 500,00", vm.DisplayRetailPrice);
        }
        finally
        {
            CurrencyDisplay.Current = previous;
        }
    }

    [Fact]
    public void ProductItemViewModel_DisplayRetailPrice_SinUSD_UsaSnapshot()
    {
        var rateMock = CreateRateMock(50.00m);
        var dto = new ProductDto
        {
            Id = 901,
            Name = "Cortesia en Bs.S",
            SKU = "BS-901",
            IsActive = true,
            PriceUSD = 0m,
            PriceBsS = 300.00m
        };
        var vm = new ProductItemViewModel(dto, rateMock.Object);

        var previous = CurrencyDisplay.Current;
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
        try
        {
            Assert.Equal("Bs.S 300,00", vm.DisplayRetailPrice);
        }
        finally
        {
            CurrencyDisplay.Current = previous;
        }
    }

    [Fact]
    public void ProductItemViewModel_DisplayRetailPrice_ConRateInvalida_UsaSnapshot()
    {
        var rateMock = CreateRateMock(0m);
        var dto = new ProductDto
        {
            Id = 902,
            Name = "Arroz",
            SKU = "ARR-902",
            IsActive = true,
            PriceUSD = 10.00m,
            PriceBsS = 100.00m
        };
        var vm = new ProductItemViewModel(dto, rateMock.Object);

        var previous = CurrencyDisplay.Current;
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
        try
        {
            Assert.Equal("Bs.S 100,00", vm.DisplayRetailPrice);
        }
        finally
        {
            CurrencyDisplay.Current = previous;
        }
    }

    [Fact]
    public void ProductItemViewModel_UpdateExchangeRate_ConNuevaTasa_ActualizaDisplay_Y_NoPisaSinUSD()
    {
        var previous = CurrencyDisplay.Current;
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
        try
        {
            // Con USD: el snapshot viejo se reemplaza por USD x tasa nueva.
            var rateMock = CreateRateMock(50.00m);
            var conUsd = new ProductItemViewModel(
                new ProductDto { Id = 903, Name = "Arroz", SKU = "ARR-903", IsActive = true, PriceUSD = 10.00m, PriceBsS = 100.00m },
                rateMock.Object);

            rateMock.Setup(e => e.CurrentRate).Returns(60.00m);
            conUsd.UpdateExchangeRate();

            Assert.Equal(600.00m, conUsd.PriceBsS);
            Assert.Equal("Bs.S 600,00", conUsd.DisplayRetailPrice);

            // Sin USD (producto Bs.S-only): la nueva tasa no debe pisar el precio con 0.
            var rateMockSinUsd = CreateRateMock(50.00m);
            var sinUsd = new ProductItemViewModel(
                new ProductDto { Id = 904, Name = "Servicio en Bs.S", SKU = "BS-904", IsActive = true, PriceUSD = 0m, PriceBsS = 300.00m },
                rateMockSinUsd.Object);

            rateMockSinUsd.Setup(e => e.CurrentRate).Returns(60.00m);
            sinUsd.UpdateExchangeRate();

            Assert.Equal(300.00m, sinUsd.PriceBsS);
            Assert.Equal("Bs.S 300,00", sinUsd.DisplayRetailPrice);
        }
        finally
        {
            CurrencyDisplay.Current = previous;
        }
    }

    [Fact]
    public void ProductItemViewModel_DisplayWholesalePrice_DerivaDeTasa_SegunMayoristaReal()
    {
        var previous = CurrencyDisplay.Current;
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
        try
        {
            // Con mayorista real: se deriva de PriceWholesaleUSD x tasa vigente.
            var rateMock = CreateRateMock(50.00m);
            var conMayorista = new ProductItemViewModel(
                new ProductDto
                {
                    Id = 905,
                    Name = "Cerveza Six Pack",
                    SKU = "CER-905",
                    IsActive = true,
                    HasWholesale = true,
                    PriceUSD = 10.00m,
                    PriceWholesaleUSD = 8.00m,
                    PriceBsS = 100.00m // Snapshot viejo
                },
                rateMock.Object);

            Assert.Equal(400.00m, conMayorista.EffectivePriceWholesaleBsS);
            Assert.Equal("Bs.S 400,00", conMayorista.DisplayWholesalePrice);

            // Sin mayorista real: el precio al mayor mostrado sigue la tasa vigente del detal.
            var sinMayorista = new ProductItemViewModel(
                new ProductDto { Id = 906, Name = "Arroz", SKU = "ARR-906", IsActive = true, PriceUSD = 10.00m, PriceBsS = 100.00m },
                rateMock.Object);

            Assert.Equal("Bs.S 500,00", sinMayorista.DisplayWholesalePrice);
        }
        finally
        {
            CurrencyDisplay.Current = previous;
        }
    }

    [Fact]
    public async Task VariantSelectionViewModel_BasePriceBsS_DerivaDeTasa_IgnoraSnapshot()
    {
        var rateMock = CreateRateMock(50.00m);
        var productMock = new Mock<IProductService>();
        productMock.Setup(s => s.GetVariantsAsync(It.IsAny<int>()))
                   .ReturnsAsync(new List<ProductDto>());

        var parentQuickInfo = new ProductQuickInfoDto
        {
            Id = 100,
            Name = "Refrescos Sabores",
            IsGroupHeader = true,
            PriceRetailUSD = 10.00m,
            PriceBsS = 100.00m // Snapshot viejo
        };

        var vm = new VariantSelectionViewModel(productMock.Object, rateMock.Object, parentQuickInfo);
        await vm.LoadVariantsAsync();

        Assert.Equal(500.00m, vm.BasePriceBsS);

        // Sin USD: cae al snapshot persistido.
        var parentSinUsd = new ProductQuickInfoDto
        {
            Id = 101,
            Name = "Servicio en Bs.S",
            PriceRetailUSD = 0m,
            PriceUSD = 0m,
            PriceBsS = 300.00m
        };

        var vmSinUsd = new VariantSelectionViewModel(productMock.Object, rateMock.Object, parentSinUsd);
        await vmSinUsd.LoadVariantsAsync();

        Assert.Equal(300.00m, vmSinUsd.BasePriceBsS);
    }
}
