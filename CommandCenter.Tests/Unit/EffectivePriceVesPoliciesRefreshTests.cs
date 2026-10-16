using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.185-T4 (S2b): Ajustes emite las políticas recién persistidas SOLO tras un PUT exitoso; el
/// payload completo permite que Catálogo y POS apliquen regla/toggle en memoria — 0 consultas.
/// </summary>
public class EffectivePriceVesPoliciesRefreshTests
{
    private sealed class PoliciesListener
    {
        public List<PricingPoliciesDto> Received { get; } = new();
    }

    private static SettingsViewModel CreateSettingsViewModel(Mock<ISettingsService> settings, Mock<IDialogService> dialog)
    {
        var payments = new Mock<IPaymentService>();
        payments.Setup(p => p.GetAllMethodsAsync()).ReturnsAsync(new List<PaymentMethodDto>());

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Admin", Role = UserRole.Admin, Cedula = "V-1", IsActive = true });

        return new SettingsViewModel(payments.Object, settings.Object, session, dialog.Object);
    }

    [Fact]
    public async Task Settings_SuccessfulSave_EmitsPricingPoliciesChangedMessageWithPersistedPayload()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetPricingPoliciesAsync()).ReturnsAsync(new PricingPoliciesDto());
        settings.Setup(s => s.SetPricingPoliciesAsync(It.IsAny<PricingPoliciesDto>())).Returns(Task.CompletedTask);

        var listener = new PoliciesListener();
        WeakReferenceMessenger.Default.Register<PricingPoliciesChangedMessage>(
            listener, (r, m) => ((PoliciesListener)r).Received.Add(m.Value));

        try
        {
            using var vm = CreateSettingsViewModel(settings, new Mock<IDialogService>());
            await vm.EnsureLoadedAsync();

            vm.EnforceMinimumMarginFloor = false;
            await vm.PricingPoliciesSaveTask;

            var emitted = Assert.Single(listener.Received);
            Assert.False(emitted.EnforceMinimumMarginFloor);
            Assert.Equal(RoundingRuleType.ExactMath, emitted.CashBsRoundingRule);
            Assert.Equal(RoundingRuleType.ExactMath, emitted.UsdRoundingRule);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(listener);
        }
    }

    [Fact]
    public async Task Settings_FailedSave_DoesNotEmitPricingPoliciesChangedMessage()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetPricingPoliciesAsync()).ReturnsAsync(new PricingPoliciesDto());
        settings.Setup(s => s.SetPricingPoliciesAsync(It.IsAny<PricingPoliciesDto>()))
                .ThrowsAsync(new InvalidOperationException("Server down"));

        var listener = new PoliciesListener();
        WeakReferenceMessenger.Default.Register<PricingPoliciesChangedMessage>(
            listener, (r, m) => ((PoliciesListener)r).Received.Add(m.Value));

        try
        {
            using var vm = CreateSettingsViewModel(settings, new Mock<IDialogService>());
            await vm.EnsureLoadedAsync();

            vm.EnforceMinimumMarginFloor = false;
            await vm.PricingPoliciesSaveTask;

            Assert.Empty(listener.Received);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(listener);
        }
    }
}
