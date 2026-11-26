using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// WU1 parity: Single regime, history loading/empty states and the read-only RBAC mirror for
/// ExchangeRateView. The tests mutate the static CurrencyDisplay profile, so they run in the
/// serialized collection and restore the previous profile/format in finally.
/// </summary>
[Collection(CurrencyDisplayStateCollection.Name)]
public sealed class ExchangeRateViewModelParityTests
{
    [Fact]
    public async Task SingleProfile_ShowsNotice_AndSkipsRateAndHistoryRequests()
    {
        var previousFormat = CurrencyDisplay.Current;
        var previousProfile = CaptureProfile();
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "VES",
                PrimaryCurrencySymbol = "Bs.S"
            });

            var service = new Mock<IExchangeRateService>();

            using var vm = new ExchangeRateViewModel(service.Object);

            Assert.True(vm.IsSingle);
            Assert.Equal(
                "El régimen monetario Single no utiliza tasa de cambio: los importes se expresan únicamente en Bs.S. No hay sincronización BCV ni carga manual disponibles.",
                vm.SingleModeNotice);
            Assert.Empty(vm.HistoryRows);
            Assert.False(vm.IsHistoryLoading);
            Assert.True(vm.HasNoHistoryRecords);

            await Task.Delay(50);

            service.Verify(s => s.GetCurrentRateAsync(), Times.Never);
            service.Verify(s => s.GetHistoryAsync(), Times.Never);
        }
        finally
        {
            RestoreProfile(previousProfile, previousFormat);
        }
    }

    [Fact]
    public void SingleModeNotice_UsesTheDisplayCurrencyCode()
    {
        var previousFormat = CurrencyDisplay.Current;
        var previousProfile = CaptureProfile();
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$"
            });

            using var vm = new ExchangeRateViewModel(new Mock<IExchangeRateService>().Object);

            Assert.Contains("únicamente en USD.", vm.SingleModeNotice, StringComparison.Ordinal);
            Assert.DoesNotContain("únicamente en $.", vm.SingleModeNotice, StringComparison.Ordinal);
        }
        finally
        {
            RestoreProfile(previousProfile, previousFormat);
        }
    }

    [Fact]
    public async Task EmptyHistory_ExposesEmptyStateWithoutRows()
    {
        var previousFormat = CurrencyDisplay.Current;
        var previousProfile = CaptureProfile();
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplyDualUsdVesProfile();

            var historyRequests = 0;
            var service = new Mock<IExchangeRateService>();
            service.Setup(s => s.GetCurrentRateAsync()).ReturnsAsync((924.36m, DateTime.UtcNow));
            service.Setup(s => s.GetHistoryAsync()).Returns(() =>
            {
                Interlocked.Increment(ref historyRequests);
                return Task.FromResult(new List<ExchangeRateHistoryDto>());
            });

            using var vm = new ExchangeRateViewModel(service.Object);

            await WaitForAsync(
                () => !vm.IsLoading && !vm.IsHistoryLoading && Volatile.Read(ref historyRequests) > 0,
                "la carga inicial debía solicitar el historial");

            Assert.False(vm.HasHistoryRecords);
            Assert.True(vm.HasNoHistoryRecords);
            Assert.Empty(vm.HistoryRows);
        }
        finally
        {
            RestoreProfile(previousProfile, previousFormat);
        }
    }

    [Fact]
    public async Task HistoryLoading_TransitionsFromLoadingToPopulated()
    {
        var previousFormat = CurrencyDisplay.Current;
        var previousProfile = CaptureProfile();
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplyDualUsdVesProfile();

            var historyCompletion = new TaskCompletionSource<List<ExchangeRateHistoryDto>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new Mock<IExchangeRateService>();
            service.Setup(s => s.GetCurrentRateAsync()).ReturnsAsync((924.36m, DateTime.UtcNow));
            service.Setup(s => s.GetHistoryAsync()).Returns(historyCompletion.Task);

            using var vm = new ExchangeRateViewModel(service.Object);

            await WaitForAsync(() => vm.IsHistoryLoading, "el historial debía quedar en carga");
            Assert.True(vm.IsHistoryLoading);
            Assert.False(vm.HasHistoryRecords);
            Assert.False(vm.HasNoHistoryRecords);

            historyCompletion.SetResult(new List<ExchangeRateHistoryDto>
            {
                new()
                {
                    Date = new DateOnly(2026, 11, 25),
                    Rate = 924.36m,
                    UpdatedAt = new DateTime(2026, 11, 25, 22, 22, 0, DateTimeKind.Utc)
                }
            });

            await WaitForAsync(
                () => !vm.IsHistoryLoading && vm.HasHistoryRecords,
                "el historial debía salir del estado de carga con una fila");

            Assert.Single(vm.HistoryRows);
            Assert.True(vm.HasHistoryRecords);
            Assert.False(vm.HasNoHistoryRecords);
        }
        finally
        {
            RestoreProfile(previousProfile, previousFormat);
        }
    }

    [Fact]
    public async Task ProfileChangeToSingle_ClearsHistory_AndBackToDualReloads()
    {
        var previousFormat = CurrencyDisplay.Current;
        var previousProfile = CaptureProfile();
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplyDualUsdVesProfile();

            var historyRequests = 0;
            var service = new Mock<IExchangeRateService>();
            service.Setup(s => s.GetCurrentRateAsync()).ReturnsAsync((924.36m, DateTime.UtcNow));
            service.Setup(s => s.GetHistoryAsync()).Returns(() =>
            {
                Interlocked.Increment(ref historyRequests);
                return Task.FromResult(new List<ExchangeRateHistoryDto>
                {
                    new()
                    {
                        Date = new DateOnly(2026, 11, 25),
                        Rate = 924.36m,
                        UpdatedAt = new DateTime(2026, 11, 25, 22, 22, 0, DateTimeKind.Utc)
                    }
                });
            });

            using var vm = new ExchangeRateViewModel(service.Object);

            await WaitForAsync(
                () => !vm.IsLoading && !vm.IsHistoryLoading && vm.HasHistoryRecords,
                "el perfil Dual debía cargar el historial");

            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "COP",
                PrimaryCurrencySymbol = "COP"
            });

            await WaitForAsync(() => vm.IsSingle && !vm.IsHistoryLoading, "el perfil cambió a Single");

            Assert.Empty(vm.HistoryRows);
            Assert.False(vm.HasHistoryRecords);
            Assert.True(vm.HasNoHistoryRecords);
            Assert.Contains("únicamente en COP.", vm.SingleModeNotice, StringComparison.Ordinal);

            var requestsInSingle = Volatile.Read(ref historyRequests);
            await Task.Delay(80);
            Assert.Equal(requestsInSingle, Volatile.Read(ref historyRequests));

            ApplyDualUsdVesProfile();

            await WaitForAsync(
                () => !vm.IsSingle
                    && !vm.IsLoading
                    && !vm.IsHistoryLoading
                    && Volatile.Read(ref historyRequests) > requestsInSingle,
                "el retorno a Dual debía recargar el historial");

            Assert.Single(vm.HistoryRows);
            Assert.True(vm.HasHistoryRecords);
        }
        finally
        {
            RestoreProfile(previousProfile, previousFormat);
        }
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromStaticProfileChanges()
    {
        var previousFormat = CurrencyDisplay.Current;
        var previousProfile = CaptureProfile();
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplyDualUsdVesProfile();

            var rateRequests = 0;
            var historyRequests = 0;
            var service = new Mock<IExchangeRateService>();
            service.Setup(s => s.GetCurrentRateAsync()).Returns(() =>
            {
                Interlocked.Increment(ref rateRequests);
                return Task.FromResult((924.36m, (DateTime?)DateTime.UtcNow));
            });
            service.Setup(s => s.GetHistoryAsync()).Returns(() =>
            {
                Interlocked.Increment(ref historyRequests);
                return Task.FromResult(new List<ExchangeRateHistoryDto>());
            });

            using var vm = new ExchangeRateViewModel(service.Object);
            await WaitForAsync(
                () => !vm.IsLoading && !vm.IsHistoryLoading && Volatile.Read(ref rateRequests) > 0,
                "la carga inicial debía completarse antes de disponer el ViewModel");

            var ratesBeforeDispose = Volatile.Read(ref rateRequests);
            var historyBeforeDispose = Volatile.Read(ref historyRequests);

            vm.Dispose();
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "COP",
                PrimaryCurrencySymbol = "COP"
            });
            ApplyDualUsdVesProfile();
            await Task.Delay(80);

            Assert.Equal(ratesBeforeDispose, Volatile.Read(ref rateRequests));
            Assert.Equal(historyBeforeDispose, Volatile.Read(ref historyRequests));
        }
        finally
        {
            RestoreProfile(previousProfile, previousFormat);
        }
    }

    [Theory]
    [InlineData(UserRole.Manager, false)]
    [InlineData(UserRole.Admin, true)]
    public async Task ReadOnlyState_FollowsCanMutateExchangeRate(UserRole role, bool canMutate)
    {
        var previousFormat = CurrencyDisplay.Current;
        var previousProfile = CaptureProfile();
        try
        {
            CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
            ApplyDualUsdVesProfile();

            var service = new Mock<IExchangeRateService>();
            service.Setup(s => s.GetCurrentRateAsync()).ReturnsAsync((924.36m, DateTime.UtcNow));
            service.Setup(s => s.GetHistoryAsync()).ReturnsAsync(new List<ExchangeRateHistoryDto>());

            var session = new UserSession();
            session.SetUser(new UserDto
            {
                Id = role == UserRole.Admin ? 1 : 2,
                Name = role.ToString(),
                Cedula = "V-1",
                Role = role,
                IsActive = true
            });

            using var vm = new ExchangeRateViewModel(service.Object, session);

            await WaitForAsync(
                () => !vm.IsLoading && !vm.IsHistoryLoading && vm.CurrentRate > 0,
                "el modo solo lectura también debía cargar la tasa vigente");

            Assert.Equal(canMutate, vm.UserSession!.CanMutateExchangeRate);
            Assert.Equal(924.36m, vm.CurrentRate);
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

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), because);
    }
}
