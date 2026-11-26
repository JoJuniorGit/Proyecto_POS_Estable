using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Helpers;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

[Collection(CurrencyDisplayStateCollection.Name)]
public sealed class ExchangeRateViewModelCommandTests
{
    [Fact]
    public async Task SaveRateCommand_PersistsTheFormattedManualRate()
    {
        var previousFormat = CurrencyDisplay.Current;
        var previousProfile = CaptureProfile();
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
        try
        {
            ApplyDualUsdVesProfile();
            var service = new Mock<IExchangeRateService>();
            service.Setup(s => s.GetCurrentRateAsync()).ReturnsAsync((924.36m, DateTime.UtcNow));
            service.Setup(s => s.GetHistoryAsync()).ReturnsAsync(new List<ExchangeRateHistoryDto>());
            service.Setup(s => s.SaveRateAsync(930.15m)).Returns(Task.CompletedTask);
            service.SetupGet(s => s.CurrentRate).Returns(930.15m);

            using var vm = new ExchangeRateViewModel(service.Object);
            WeakReferenceMessenger.Default.UnregisterAll(vm);
            await WaitForAsync(() => !vm.IsLoading && vm.CurrentRate > 0);

            vm.NewRateText = "930,15";
            await vm.SaveRateCommand.ExecuteAsync(null);

            service.Verify(s => s.SaveRateAsync(930.15m), Times.Once);
            Assert.Equal(930.15m, vm.CurrentRate);
            Assert.Equal("930,15", vm.NewRateText);
            Assert.Equal("Tasa de cambio actualizada correctamente.", vm.StatusMessage);
        }
        finally
        {
            RestoreProfile(previousProfile, previousFormat);
        }
    }

    [Fact]
    public async Task SyncBcvCommand_UpdatesRateAndHistory()
    {
        var previousFormat = CurrencyDisplay.Current;
        var previousProfile = CaptureProfile();
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
        try
        {
            ApplyDualUsdVesProfile();
            var service = new Mock<IExchangeRateService>();
            service.Setup(s => s.GetCurrentRateAsync()).ReturnsAsync((924.36m, DateTime.UtcNow));
            service.Setup(s => s.GetHistoryAsync()).ReturnsAsync(new List<ExchangeRateHistoryDto>());
            service.Setup(s => s.SyncBcvAsync()).ReturnsAsync((930.15m, DateTime.UtcNow));

            using var vm = new ExchangeRateViewModel(service.Object);
            WeakReferenceMessenger.Default.UnregisterAll(vm);
            await WaitForAsync(() => !vm.IsLoading && vm.CurrentRate > 0);

            await vm.SyncBcvCommand.ExecuteAsync(null);

            service.Verify(s => s.SyncBcvAsync(), Times.Once);
            Assert.Equal(930.15m, vm.CurrentRate);
            Assert.Equal("Tasa sincronizada con BCV: Bs.S 930,1500", vm.StatusMessage);
            service.Verify(s => s.GetHistoryAsync(), Times.AtLeast(2));
        }
        finally
        {
            RestoreProfile(previousProfile, previousFormat);
        }
    }

    [Fact]
    public async Task InitialLoad_ShowsFourDecimalRateAndActualizadaStatus()
    {
        var previousFormat = CurrencyDisplay.Current;
        var previousProfile = CaptureProfile();
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
        try
        {
            ApplyDualUsdVesProfile();
            var service = new Mock<IExchangeRateService>();
            service.Setup(s => s.GetCurrentRateAsync()).ReturnsAsync((924.36m, DateTime.UtcNow));
            service.Setup(s => s.GetHistoryAsync()).ReturnsAsync(new List<ExchangeRateHistoryDto>());

            using var vm = new ExchangeRateViewModel(service.Object);
            WeakReferenceMessenger.Default.UnregisterAll(vm);
            await WaitForAsync(() => !vm.IsLoading && !vm.IsHistoryLoading && vm.CurrentRate > 0);

            Assert.Equal("924,3600", vm.CurrentRateDisplay);
            Assert.StartsWith("Actualizada: ", vm.LastUpdatedStatusText, StringComparison.Ordinal);
        }
        finally
        {
            RestoreProfile(previousProfile, previousFormat);
        }
    }

    private static void ApplyDualUsdVesProfile() => CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "VES",
        SecondaryCurrencySymbol = "Bs.S"
    });

    private static MonetaryProfileDto CaptureProfile() => new()
    {
        Mode = CurrencyDisplay.Mode,
        PrimaryCurrencyCode = CurrencyDisplay.PrimaryCurrencyCode,
        PrimaryCurrencySymbol = CurrencyDisplay.PrimaryCurrencySymbol,
        SecondaryCurrencyCode = CurrencyDisplay.SecondaryCurrencyCode,
        SecondaryCurrencySymbol = CurrencyDisplay.SecondaryCurrencySymbol
    };

    private static void RestoreProfile(MonetaryProfileDto profile, MoneyDisplayFormat format)
    {
        CurrencyDisplay.Current = format;
        CurrencyDisplay.SetMonetaryProfile(profile);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }
    }
}
