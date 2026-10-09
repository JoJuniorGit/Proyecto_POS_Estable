using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Interfaces;
using MediatR;
using Moq;
using Sales.Module.Data;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.152 (SEC-08): anclaje de la tasa del checkout-preview. El método público
/// ResolveCheckoutRateAsync delega en el resolver compartido del completar venta
/// (tolerancia configurable, ancla a la BCV del día, rechazo ≥ ±100% y fail-open).
/// </summary>
public class CheckoutPreviewRateAnchorTests
{
    private const int SaleId = 42;
    private const decimal OfficialRate = 50m;

    private static SalesService CreateService(
        SalesDbContext context,
        IInventoryService inventory,
        ISystemSettingsService settings)
        => new(
            context,
            inventory,
            new Mock<IMediator>().Object,
            new Mock<ICashDrawerService>().Object,
            settings);

    private static Mock<IInventoryService> CreateInventoryMock(decimal officialRate)
    {
        var mock = new Mock<IInventoryService>();
        mock.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(officialRate);
        return mock;
    }

    private static Mock<ISystemSettingsService> CreateSettingsMock(string? toleranceSetting = null)
    {
        var mock = new Mock<ISystemSettingsService>();
        mock.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync(toleranceSetting);
        return mock;
    }

    [Fact]
    public async Task ResolveCheckoutRateAsync_WhenDeviationWithinTolerance_ReturnsClientRate()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context, CreateInventoryMock(OfficialRate).Object, CreateSettingsMock().Object);

        var rate = await service.ResolveCheckoutRateAsync(SaleId, 52m);

        Assert.Equal(52m, rate);
    }

    [Fact]
    public async Task ResolveCheckoutRateAsync_WhenDeviationWithinTolerance_RoundsClientRateCeiling()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context, CreateInventoryMock(OfficialRate).Object, CreateSettingsMock().Object);

        var rate = await service.ResolveCheckoutRateAsync(SaleId, 52.001m);

        Assert.Equal(52.01m, rate);
    }

    [Fact]
    public async Task ResolveCheckoutRateAsync_WhenDeviationExceedsTolerance_AnchorsToOfficialBcvRate()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context, CreateInventoryMock(OfficialRate).Object, CreateSettingsMock().Object);

        var rate = await service.ResolveCheckoutRateAsync(SaleId, 60m);

        Assert.Equal(OfficialRate, rate);
    }

    // Límite exacto: desvío 1.0 (100 vs 50) también rechaza (>= ±100%).
    [Theory]
    [InlineData(100)]
    [InlineData(120)]
    public async Task ResolveCheckoutRateAsync_WhenDeviationReachesOneHundredPercent_ThrowsArgumentExceptionWithExactMessage(decimal clientRate)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context, CreateInventoryMock(OfficialRate).Object, CreateSettingsMock().Object);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.ResolveCheckoutRateAsync(SaleId, clientRate));

        Assert.Equal(
            $"La tasa de cambio {clientRate} fue rechazada: excede ±100% de la tasa BCV oficial ({OfficialRate}). Contacte al supervisor.",
            ex.Message);
    }

    [Fact]
    public async Task ResolveCheckoutRateAsync_WhenBcvRateIsZero_FailsOpenWithClientRate()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context, CreateInventoryMock(0m).Object, CreateSettingsMock().Object);

        var rate = await service.ResolveCheckoutRateAsync(SaleId, 77m);

        Assert.Equal(77m, rate);
    }

    [Fact]
    public async Task ResolveCheckoutRateAsync_WhenBcvLookupThrows_FailsOpenWithRoundedClientRate()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("BCV no disponible"));
        var service = CreateService(context, inventory.Object, CreateSettingsMock().Object);

        var rate = await service.ResolveCheckoutRateAsync(SaleId, 36.502175m);

        Assert.Equal(36.51m, rate);
    }

    [Fact]
    public async Task ResolveCheckoutRateAsync_WhenConfiguredToleranceIsNarrower_AnchorsDeviationAboveIt()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context, CreateInventoryMock(OfficialRate).Object, CreateSettingsMock("0.05").Object);

        // Desvío 6% (53 vs 50): supera la tolerancia configurada 0.05 -> se ancla a la BCV.
        var rate = await service.ResolveCheckoutRateAsync(SaleId, 53m);

        Assert.Equal(OfficialRate, rate);
    }

    [Fact]
    public async Task ResolveCheckoutRateAsync_WhenTokenAlreadyCancelled_ThrowsBeforeQueryingBcv()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var inventory = CreateInventoryMock(OfficialRate);
        var service = CreateService(context, inventory.Object, CreateSettingsMock().Object);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ResolveCheckoutRateAsync(SaleId, 52m, new CancellationToken(canceled: true)));

        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
