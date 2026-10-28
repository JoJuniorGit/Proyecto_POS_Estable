using System;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Interfaces;
using Moq;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3c3b1 (S4/S12): the shared exchange-rate anchor resolves identity in Single mode
/// before any client-rate validation or BCV lookup, while Dual/null profiles preserve the
/// existing official-rate anchor behavior (fail-open included).
/// </summary>
public class ExchangeRateAnchorServiceSingleTests
{
    private const string ContextLabel = "AnchorSingleTest";
    private const int ReferenceId = 7;

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

    [Fact]
    public async Task ResolveAsync_SingleProfile_ReturnsIdentityWithoutQueryingOfficialRate()
    {
        var inventory = CreateInventoryMock(4000m);
        var profile = CreateProfileMock(CreateProfile(MonetaryMode.Single, "COP", null));
        var service = new ExchangeRateAnchorService(
            inventory.Object, CreateSettingsMock().Object, logger: null, profile.Object);

        var result = await service.ResolveAsync(4000m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Identity, result.Outcome);
        Assert.Equal(1m, result.EffectiveRate);
        Assert.Equal(0m, result.OfficialRate);
        Assert.Equal(4000m, result.ClientRate);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task ResolveAsync_SingleProfile_ResolvesIdentityBeforeClientRateValidation(decimal clientRate)
    {
        var inventory = CreateInventoryMock(4000m);
        var profile = CreateProfileMock(CreateProfile(MonetaryMode.Single, "COP", null));
        var service = new ExchangeRateAnchorService(
            inventory.Object, CreateSettingsMock().Object, logger: null, profile.Object);

        var result = await service.ResolveAsync(clientRate, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Identity, result.Outcome);
        Assert.Equal(1m, result.EffectiveRate);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DualProfile_KeepsAcceptedOfficialRateBehavior()
    {
        var inventory = CreateInventoryMock(50m);
        var profile = CreateProfileMock(CreateProfile(MonetaryMode.Dual));
        var service = new ExchangeRateAnchorService(
            inventory.Object, CreateSettingsMock().Object, logger: null, profile.Object);

        var result = await service.ResolveAsync(52m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Accepted, result.Outcome);
        Assert.Equal(52m, result.EffectiveRate);
        Assert.Equal(50m, result.OfficialRate);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_DualProfile_KeepsAnchoredOfficialRateBehavior()
    {
        var inventory = CreateInventoryMock(50m);
        var profile = CreateProfileMock(CreateProfile(MonetaryMode.Dual));
        var service = new ExchangeRateAnchorService(
            inventory.Object, CreateSettingsMock().Object, logger: null, profile.Object);

        var result = await service.ResolveAsync(60m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Anchored, result.Outcome);
        Assert.Equal(50m, result.EffectiveRate);
        Assert.Equal(50m, result.OfficialRate);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_NullProfileService_KeepsOfficialRateBehavior()
    {
        var inventory = CreateInventoryMock(50m);
        var service = new ExchangeRateAnchorService(inventory.Object, CreateSettingsMock().Object);

        var result = await service.ResolveAsync(52m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Accepted, result.Outcome);
        Assert.Equal(52m, result.EffectiveRate);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_ProfileReadThrows_FailsOpenToOfficialAnchorBehavior()
    {
        var inventory = CreateInventoryMock(50m);
        var profile = new Mock<IMonetaryProfileService>();
        profile.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("profile store unavailable"));
        var service = new ExchangeRateAnchorService(
            inventory.Object, CreateSettingsMock().Object, logger: null, profile.Object);

        var result = await service.ResolveAsync(60m, ContextLabel, ReferenceId);

        Assert.Equal(ExchangeRateAnchorOutcome.Anchored, result.Outcome);
        Assert.Equal(50m, result.EffectiveRate);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_ProfileReadCancelled_RethrowsWithoutQueryingOfficialRate()
    {
        var inventory = CreateInventoryMock(50m);
        var profile = new Mock<IMonetaryProfileService>();
        profile.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var service = new ExchangeRateAnchorService(
            inventory.Object, CreateSettingsMock().Object, logger: null, profile.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ResolveAsync(60m, ContextLabel, ReferenceId));

        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
