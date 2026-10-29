using System;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Helpers;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Moq;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3c3c1 (S4/S12): Dual USD/non-VES resolves the manual daily pair quote through
/// <see cref="ICurrencyRateService"/>, keeps the shared ±100%/tolerance kernel, and fails closed
/// when the quote or the service is missing. USD/VES keeps BCV and Single keeps identity.
/// </summary>
public class ExchangeRateAnchorServiceDualTests
{
    private const string ContextLabel = "AnchorDualTest";
    private const int ReferenceId = 11;
    private const string CopCode = "COP";

    private static MonetaryProfileDto CreateProfile(
        MonetaryMode mode,
        string primaryCode = "USD",
        string? secondaryCode = "VES")
        => new()
        {
            Mode = mode,
            PrimaryCurrencyCode = primaryCode,
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = secondaryCode,
            SecondaryCurrencySymbol = secondaryCode is null ? null : "Bs.S",
            IsConfigured = true
        };

    private static Mock<IMonetaryProfileService> CreateProfileMock(MonetaryProfileDto profile)
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        return mock;
    }

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

    private static Mock<ICurrencyRateService> CreateManualRateMock(
        decimal rate,
        CurrencyRateSource source = CurrencyRateSource.Manual)
    {
        var mock = new Mock<ICurrencyRateService>();
        mock.Setup(r => r.GetRateAsync("USD", CopCode, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(
                rate,
                source,
                DateOnly.FromDateTime(DateTime.UtcNow),
                "USD",
                CopCode));
        return mock;
    }

    private static ExchangeRateAnchorService CreateService(
        Mock<IMonetaryProfileService> profile,
        Mock<IInventoryService> inventory,
        Mock<ISystemSettingsService>? settings = null,
        Mock<ICurrencyRateService>? rateService = null)
        => new(
            inventory.Object,
            (settings ?? CreateSettingsMock()).Object,
            logger: null,
            profile.Object,
            rateService?.Object);

    [Fact]
    public async Task ResolveAsync_DualUsdCopWithMatchingManualRate_ReturnsAcceptedManual()
    {
        var inventory = CreateInventoryMock(4000m);
        var rateService = CreateManualRateMock(4000m);
        var service = CreateService(
            CreateProfileMock(CreateProfile(MonetaryMode.Dual, "USD", CopCode)),
            inventory,
            rateService: rateService);

        var result = await service.ResolveAsync(4000m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Accepted, result.Outcome);
        Assert.Equal(4000m, result.EffectiveRate);
        Assert.Equal(4000m, result.OfficialRate);
        Assert.Equal(CurrencyRateSource.Manual, result.RateSource);
        rateService.Verify(
            r => r.GetRateAsync("USD", CopCode, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DualUsdCopAboveTolerance_AnchorsToManualRate()
    {
        // 4400 vs 4000 = 10%; with a 5% configured tolerance it must anchor to the manual quote.
        var inventory = CreateInventoryMock(4000m);
        var service = CreateService(
            CreateProfileMock(CreateProfile(MonetaryMode.Dual, "USD", CopCode)),
            inventory,
            CreateSettingsMock("0.05"),
            CreateManualRateMock(4000m));

        var result = await service.ResolveAsync(4400m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Anchored, result.Outcome);
        Assert.Equal(4000m, result.EffectiveRate);
        Assert.Equal(4400m, result.ClientRate);
        Assert.Equal(4000m, result.OfficialRate);
        Assert.Equal(CurrencyRateSource.Manual, result.RateSource);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DualUsdCopAtDoubleDeviation_Rejects()
    {
        var inventory = CreateInventoryMock(4000m);
        var service = CreateService(
            CreateProfileMock(CreateProfile(MonetaryMode.Dual, "USD", CopCode)),
            inventory,
            rateService: CreateManualRateMock(4000m));

        var result = await service.ResolveAsync(8000m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Rejected, result.Outcome);
        Assert.Equal(8000m, result.EffectiveRate);
        Assert.Equal(4000m, result.OfficialRate);
        Assert.Equal(CurrencyRateSource.Manual, result.RateSource);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DualUsdCopManualInverse_PropagatesInverseSource()
    {
        var service = CreateService(
            CreateProfileMock(CreateProfile(MonetaryMode.Dual, "USD", CopCode)),
            CreateInventoryMock(4000m),
            rateService: CreateManualRateMock(0.00025m, CurrencyRateSource.ManualInverse));

        var result = await service.ResolveAsync(0.00025m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Accepted, result.Outcome);
        Assert.Equal(CurrencyRateSource.ManualInverse, result.RateSource);
    }

    [Fact]
    public async Task ResolveAsync_DualUsdCopMissingManualRate_PropagatesFailClosed()
    {
        var inventory = CreateInventoryMock(4000m);
        var rateService = new Mock<ICurrencyRateService>();
        rateService
            .Setup(r => r.GetRateAsync("USD", CopCode, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No hay tasa manual configurada para USD->COP en la fecha 2026-10-28."));
        var service = CreateService(
            CreateProfileMock(CreateProfile(MonetaryMode.Dual, "USD", CopCode)),
            inventory,
            rateService: rateService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ResolveAsync(4000m, ContextLabel, ReferenceId));

        Assert.Contains("No hay tasa manual configurada", exception.Message);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DualUsdCopWithoutRateService_FailsClosed()
    {
        var inventory = CreateInventoryMock(4000m);
        var service = CreateService(
            CreateProfileMock(CreateProfile(MonetaryMode.Dual, "USD", CopCode)),
            inventory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ResolveAsync(4000m, ContextLabel, ReferenceId));

        Assert.Contains("No hay servicio de tasas manuales configurado para el par USD/COP", exception.Message);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DualUsdVes_KeepsBcvPathAndSource()
    {
        var inventory = CreateInventoryMock(50m);
        var rateService = new Mock<ICurrencyRateService>();
        var service = CreateService(
            CreateProfileMock(CreateProfile(MonetaryMode.Dual, "USD", "VES")),
            inventory,
            rateService: rateService);

        var result = await service.ResolveAsync(52m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Accepted, result.Outcome);
        Assert.Equal(52m, result.EffectiveRate);
        Assert.Equal(50m, result.OfficialRate);
        Assert.Equal(CurrencyRateSource.Bcv, result.RateSource);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Once);
        rateService.Verify(
            r => r.GetRateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_SingleProfile_ReturnsIdentitySource()
    {
        var inventory = CreateInventoryMock(4000m);
        var service = CreateService(
            CreateProfileMock(CreateProfile(MonetaryMode.Single, "USD", null)),
            inventory);

        var result = await service.ResolveAsync(999m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Identity, result.Outcome);
        Assert.Equal(1m, result.EffectiveRate);
        Assert.Equal(CurrencyRateSource.Identity, result.RateSource);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DualUsdCop_WithRealCurrencyRateService_ResolvesTodaysManualPair()
    {
        // Proves the manual pair resolution is not mock-only: a real CurrencyPairRate row for the
        // current Venezuela date is resolved through the production CurrencyRateService.
        using InventoryDbContext inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        inventoryDb.CurrencyPairRates.Add(new CurrencyPairRate
        {
            RateDate = TimeZoneHelper.GetVenezuelaDate(),
            FromCurrencyCode = "USD",
            ToCurrencyCode = CopCode,
            Rate = 4000m,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await inventoryDb.SaveChangesAsync();

        var inventory = CreateInventoryMock(50m);
        var service = new ExchangeRateAnchorService(
            inventory.Object,
            CreateSettingsMock().Object,
            logger: null,
            CreateProfileMock(CreateProfile(MonetaryMode.Dual, "USD", CopCode)).Object,
            new CurrencyRateService(inventoryDb));

        var result = await service.ResolveAsync(4000m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Accepted, result.Outcome);
        Assert.Equal(4000m, result.EffectiveRate);
        Assert.Equal(4000m, result.OfficialRate);
        Assert.Equal(CurrencyRateSource.Manual, result.RateSource);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DualUsdCop_WithRealCurrencyRateServiceAndNoRowForToday_FailsClosed()
    {
        using InventoryDbContext inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        inventoryDb.CurrencyPairRates.Add(new CurrencyPairRate
        {
            RateDate = TimeZoneHelper.GetVenezuelaDate().AddDays(-1),
            FromCurrencyCode = "USD",
            ToCurrencyCode = CopCode,
            Rate = 4000m,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await inventoryDb.SaveChangesAsync();

        var inventory = CreateInventoryMock(50m);
        var service = new ExchangeRateAnchorService(
            inventory.Object,
            CreateSettingsMock().Object,
            logger: null,
            CreateProfileMock(CreateProfile(MonetaryMode.Dual, "USD", CopCode)).Object,
            new CurrencyRateService(inventoryDb));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ResolveAsync(4000m, ContextLabel, ReferenceId));

        Assert.Contains("No hay tasa manual configurada para USD->COP", exception.Message);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
