using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Inventory.Module.Services;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T2b (S5): identity conversion is exact and never resolves a rate; cross-currency
// conversion applies the pair quote and the destination currency precision.
public class MonetaryConversionServiceTests
{
    private const string MalformedFromMessage =
        "El código de la moneda origen debe ser un código ISO 4217 de tres letras.";
    private const string MalformedToMessage =
        "El código de la moneda destino debe ser un código ISO 4217 de tres letras.";
    private const string UnsupportedZzzMessage =
        "El código 'ZZZ' no es un código ISO 4217 reconocido.";

    private static readonly DateOnly RateDate = new(2026, 10, 20);

    private static Mock<ICurrencyRateService> CreateRateService()
        => new(MockBehavior.Strict);

    private static CurrencyRateQuote Quote(string from, string to, decimal rate)
        => new(rate, CurrencyRateSource.Manual, RateDate, from, to);

    private static void VerifyNoRateLookup(Mock<ICurrencyRateService> rateService)
        => rateService.Verify(
            x => x.GetRateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateOnly?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

    [Fact]
    public void Constructor_NullRateService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MonetaryConversionService(null!));
    }

    [Theory]
    [InlineData("USD", "USD", MonetaryAmountKind.Digital)]
    [InlineData("usd", "USD", MonetaryAmountKind.Cash)]
    [InlineData("VES", "ves", MonetaryAmountKind.Cash)]
    public async Task ConvertAsync_SameCurrency_ReturnsExactAmountWithoutRateLookupOrRounding(
        string from,
        string to,
        MonetaryAmountKind kind)
    {
        var rateService = CreateRateService();
        var service = new MonetaryConversionService(rateService.Object);
        const decimal amount = 1.23456789m;

        var result = await service.ConvertAsync(amount, from, to, kind, RateDate);

        Assert.Equal(amount, result);
        VerifyNoRateLookup(rateService);
    }

    [Theory]
    [InlineData("Bs.S", "VES", MonetaryAmountKind.Digital)]
    [InlineData("VES", "Bs.S", MonetaryAmountKind.Digital)]
    [InlineData("BsS", "Bs.S", MonetaryAmountKind.Cash)]
    public async Task ConvertAsync_LocalCurrencyAliases_ReturnsExactAmountWithoutRateLookup(
        string from,
        string to,
        MonetaryAmountKind kind)
    {
        var rateService = CreateRateService();
        var service = new MonetaryConversionService(rateService.Object);
        const decimal amount = 1.23456789m;

        var result = await service.ConvertAsync(amount, from, to, kind, RateDate);

        Assert.Equal(amount, result);
        VerifyNoRateLookup(rateService);
    }

    [Fact]
    public async Task ConvertAsync_FromLocalAlias_UsesCanonicalIsoForRateLookup()
    {
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("VES", "USD", RateDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quote("VES", "USD", 0.025m));
        var service = new MonetaryConversionService(rateService.Object);

        var result = await service.ConvertAsync(10m, "Bs.S", "USD", rateDate: RateDate);

        Assert.Equal(0.25m, result);
        rateService.Verify(
            x => x.GetRateAsync("VES", "USD", RateDate, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConvertAsync_SameCurrencyNegative_PreservesExactValueAndScaleWithoutRateLookup()
    {
        var rateService = CreateRateService();
        var service = new MonetaryConversionService(rateService.Object);
        const decimal amount = -1.2300m;

        var result = await service.ConvertAsync(amount, "USD", "USD", MonetaryAmountKind.Cash, RateDate);

        Assert.Equal(amount, result);
        Assert.Equal(
            amount.ToString(CultureInfo.InvariantCulture),
            result.ToString(CultureInfo.InvariantCulture));
        VerifyNoRateLookup(rateService);
    }

    [Fact]
    public async Task ConvertAsync_DifferentCurrencies_UsesQuoteRateAndDestinationDigitalPrecision()
    {
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("USD", "EUR", RateDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quote("USD", "EUR", 0.92m));
        var service = new MonetaryConversionService(rateService.Object);

        var result = await service.ConvertAsync(10.555m, "usd", "eur", rateDate: RateDate);

        Assert.Equal(9.71m, result);
        rateService.Verify(
            x => x.GetRateAsync("USD", "EUR", RateDate, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConvertAsync_ToLocalAlias_CallsRateServiceWithCanonicalVes()
    {
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("USD", "VES", RateDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quote("USD", "VES", 36.50m));
        var service = new MonetaryConversionService(rateService.Object);

        var result = await service.ConvertAsync(10m, "USD", "Bs.S", rateDate: RateDate);

        Assert.Equal(365m, result);
        rateService.Verify(
            x => x.GetRateAsync("USD", "VES", RateDate, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConvertAsync_ToZeroDecimalCurrency_RoundsToZeroDecimals()
    {
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("USD", "JPY", RateDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quote("USD", "JPY", 150m));
        var service = new MonetaryConversionService(rateService.Object);

        var result = await service.ConvertAsync(10.555m, "USD", "JPY", rateDate: RateDate);

        Assert.Equal(1583m, result);
    }

    [Fact]
    public async Task ConvertAsync_ToThreeDecimalCurrency_RoundsToThreeDecimals()
    {
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("USD", "KWD", RateDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quote("USD", "KWD", 0.3075m));
        var service = new MonetaryConversionService(rateService.Object);

        var result = await service.ConvertAsync(10.555m, "USD", "KWD", rateDate: RateDate);

        Assert.Equal(3.246m, result);
    }

    [Fact]
    public async Task ConvertAsync_ToFourDecimalCurrency_RoundsToFourDecimals()
    {
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("USD", "CLF", RateDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quote("USD", "CLF", 0.025m));
        var service = new MonetaryConversionService(rateService.Object);

        var result = await service.ConvertAsync(10.5555m, "USD", "CLF", rateDate: RateDate);

        Assert.Equal(0.2639m, result);
    }

    [Fact]
    public async Task ConvertAsync_CashToVes_RoundsToZeroDecimals()
    {
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("USD", "VES", RateDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quote("USD", "VES", 36.50m));
        var service = new MonetaryConversionService(rateService.Object);

        var result = await service.ConvertAsync(
            10.555m,
            "USD",
            "VES",
            MonetaryAmountKind.Cash,
            RateDate);

        Assert.Equal(385m, result);
    }

    [Fact]
    public async Task ConvertAsync_CashToTwoDecimalCurrency_RoundsToTwoDecimals()
    {
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("USD", "EUR", RateDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quote("USD", "EUR", 0.92m));
        var service = new MonetaryConversionService(rateService.Object);

        var result = await service.ConvertAsync(
            10.5555m,
            "USD",
            "EUR",
            MonetaryAmountKind.Cash,
            RateDate);

        Assert.Equal(9.71m, result);
    }

    [Fact]
    public async Task ConvertAsync_NegativeAmount_RoundsAwayFromZero()
    {
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("USD", "JPY", RateDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quote("USD", "JPY", 150m));
        var service = new MonetaryConversionService(rateService.Object);

        var result = await service.ConvertAsync(-10.55m, "USD", "JPY", rateDate: RateDate);

        Assert.Equal(-1583m, result);
    }

    public static TheoryData<string, string, string> InvalidCodes => new()
    {
        { "US", "EUR", MalformedFromMessage },
        { "", "EUR", MalformedFromMessage },
        { "EUR", "EU", MalformedToMessage },
        { "EUR", "E1R", MalformedToMessage },
        { "ZZZ", "EUR", UnsupportedZzzMessage },
        { "EUR", "ZZZ", UnsupportedZzzMessage },
        { "ZZZ", "ZZZ", UnsupportedZzzMessage }
    };

    [Theory]
    [MemberData(nameof(InvalidCodes))]
    public async Task ConvertAsync_InvalidCode_ThrowsExactMessageWithoutRateLookup(
        string from,
        string to,
        string expectedMessage)
    {
        var rateService = CreateRateService();
        var service = new MonetaryConversionService(rateService.Object);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.ConvertAsync(1m, from, to, rateDate: RateDate));

        Assert.Equal(expectedMessage, exception.Message);
        VerifyNoRateLookup(rateService);
    }

    [Fact]
    public async Task ConvertAsync_WhenRateIsMissing_PropagatesInvalidOperationException()
    {
        const string expectedMessage =
            "No hay tasa manual configurada para USD->EUR en la fecha 2026-10-20.";
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("USD", "EUR", RateDate, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(expectedMessage));
        var service = new MonetaryConversionService(rateService.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ConvertAsync(1m, "USD", "EUR", rateDate: RateDate));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public async Task ConvertAsync_ForwardsRateDateToRateService()
    {
        var rateService = CreateRateService();
        rateService
            .Setup(x => x.GetRateAsync("USD", "EUR", RateDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quote("USD", "EUR", 1m));
        var service = new MonetaryConversionService(rateService.Object);

        await service.ConvertAsync(1m, "USD", "EUR", rateDate: RateDate);

        rateService.Verify(
            x => x.GetRateAsync("USD", "EUR", RateDate, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
