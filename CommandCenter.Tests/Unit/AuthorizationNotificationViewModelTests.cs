using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.150 (T10, design D7): cola de notificaciones admin del WPF. Determinista: sin timers reales
/// (IAuthorizationCountdownTimer fake) y sin red (hub mockeado). Paridad con T8 Web.
/// </summary>
public class AuthorizationNotificationViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeCountdownTimer : IAuthorizationCountdownTimer
    {
        private Action? _tick;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool Disposed { get; private set; }
        public TimeSpan? LastInterval { get; private set; }

        public void Start(TimeSpan interval, Action onTick)
        {
            StartCount++;
            LastInterval = interval;
            _tick = onTick;
        }

        public void Stop() => StopCount++;

        public void Dispose() => Disposed = true;

        public void RaiseTick() => _tick?.Invoke();
    }

    private sealed class Harness
    {
        public Harness()
        {
            Hub = new Mock<IAuthorizationHubService>();
            Timer = new FakeCountdownTimer();
            Vm = new AuthorizationNotificationViewModel(
                Hub.Object,
                new InlineDispatcherInvoker(),
                Timer,
                () => Now);
            Vm.Activate();
        }

        public Mock<IAuthorizationHubService> Hub { get; }
        public FakeCountdownTimer Timer { get; }
        public AuthorizationNotificationViewModel Vm { get; }
        public DateTimeOffset Now { get; set; } = Start;

        public void RaiseRequested(AuthorizationRequestedPayload payload)
        {
            Hub.Raise(h => h.AuthorizationRequested += null, payload);
        }

        public void RaiseResolved(int requestId, string? resolvedByName = "Admin Uno", bool approved = true)
        {
            Hub.Raise(
                h => h.AuthorizationResolved += null,
                new AuthorizationResolvedPayload
                {
                    RequestId = requestId,
                    Status = approved ? "Approved" : "Rejected",
                    Approved = approved,
                    ResolvedByName = resolvedByName
                });
        }

        public void RaiseResolvedStatus(int requestId, string status, string? resolvedByName = null)
        {
            Hub.Raise(
                h => h.AuthorizationResolved += null,
                new AuthorizationResolvedPayload
                {
                    RequestId = requestId,
                    Status = status,
                    Approved = string.Equals(status, "Approved", StringComparison.OrdinalIgnoreCase),
                    ResolvedByName = resolvedByName
                });
        }

        public void RaiseReconnected()
        {
            Hub.Raise(h => h.Reconnected += null, EventArgs.Empty);
        }

        public void RaiseExpired(int requestId)
        {
            Hub.Raise(
                h => h.AuthorizationExpired += null,
                new AuthorizationExpiredPayload { RequestId = requestId, Status = "Expired" });
        }
    }

    private static AuthorizationRequestedPayload BuildRequested(
        int requestId,
        string? cashier = "Cajero 01",
        int? saleId = 445,
        string? terminal = "Caja-01",
        string? actionType = "ManualPriceOverride",
        DateTimeOffset? expiresAt = null,
        JsonElement? context = null)
        => new()
        {
            RequestId = requestId,
            ActionType = actionType,
            SaleId = saleId,
            RequestedByName = cashier,
            Terminal = terminal,
            Context = context,
            CreatedAt = Start,
            ExpiresAt = expiresAt ?? Start.AddSeconds(60)
        };

    private static JsonElement Context(
        string productName = "Café molido",
        decimal quantity = 2m,
        decimal? usd = 1.5m,
        decimal? local = 55.5m)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["productName"] = productName,
            ["quantity"] = quantity,
            ["customUnitPriceUsd"] = usd,
            ["customUnitPriceLocal"] = local
        });

    // ── Cola enriquecida ───────────────────────────────────────────────────

    [Fact]
    public void RequestedEvent_QueuesEnrichedNotificationWithExactMessageAndCountdown()
    {
        var h = new Harness();

        h.RaiseRequested(BuildRequested(1, context: Context()));

        Assert.Equal(1, h.Vm.QueuedCount);
        Assert.True(h.Vm.HasPendingNotifications);
        Assert.False(h.Vm.ShowQueueBadge);
        Assert.NotNull(h.Vm.CurrentRequest);
        Assert.Equal(1, h.Vm.CurrentRequest!.RequestId);
        Assert.Equal(
            "El cajero Cajero 01 solicita autorización para modificar el precio manual en la Factura #445."
            + " Producto: Café molido × 2. Precio USD: $ 1.50. Precio Bs.S: 55.50",
            h.Vm.CurrentMessage);
        Assert.Equal("Caja-01", h.Vm.CurrentTerminalDisplay);
        Assert.Equal(60, h.Vm.RemainingSeconds);
        Assert.Equal("01:00", h.Vm.RemainingTimeDisplay);
        Assert.Equal(1, h.Timer.StartCount);
        Assert.Equal(TimeSpan.FromMilliseconds(500), h.Timer.LastInterval);
    }

    [Fact]
    public void Message_DegradesGracefullyWithoutCashierSaleIdOrContext()
    {
        Assert.Equal(
            "Un cajero solicita autorización para modificar el precio manual.",
            AuthorizationNotificationViewModel.BuildNotificationMessage(
                BuildRequested(1, cashier: null, saleId: null, actionType: "ManualPriceOverride", context: null)));

        Assert.Equal(
            "El cajero Cajero 02 solicita autorización para SaleCancellation en la Factura #12.",
            AuthorizationNotificationViewModel.BuildNotificationMessage(
                BuildRequested(2, cashier: "Cajero 02", saleId: 12, actionType: "SaleCancellation", context: null)));
    }

    [Fact]
    public void MultipleRequests_AreQueuedInArrivalOrderWithPendingBadge()
    {
        var h = new Harness();

        h.RaiseRequested(BuildRequested(1, cashier: "Cajero 01"));
        h.RaiseRequested(BuildRequested(2, cashier: "Cajero 02", saleId: 446));
        h.RaiseRequested(BuildRequested(3, cashier: "Cajero 03", saleId: 447));

        Assert.Equal(new[] { 1, 2, 3 }, System.Linq.Enumerable.Select(h.Vm.PendingRequests, i => i.RequestId));
        Assert.Equal(3, h.Vm.QueuedCount);
        Assert.True(h.Vm.ShowQueueBadge);
        Assert.Equal("3 solicitudes pendientes", h.Vm.QueueBadgeText);
        Assert.Equal(1, h.Vm.CurrentRequest!.RequestId);
    }

    [Fact]
    public void MalformedAndDuplicatePayloads_AreIgnored()
    {
        var h = new Harness();

        h.RaiseRequested(BuildRequested(0));
        h.RaiseRequested(BuildRequested(1));
        h.RaiseRequested(BuildRequested(1, cashier: "Duplicado"));

        Assert.Equal(1, h.Vm.QueuedCount);
        Assert.Equal("Cajero 01", h.Vm.CurrentRequest!.RequestedByName);
    }

    // ── Resolucion remota y carrera ────────────────────────────────────────

    [Fact]
    public async Task Approve_ResolvesHeadAndRemovesItSilently()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.ResolveAuthorizationAsync(1, true, null))
            .ReturnsAsync(new AuthorizationResolveInfo("Approved", "Yo Mismo", null));

        await h.Vm.ApproveCommand.ExecuteAsync(null);

        h.Hub.Verify(x => x.ResolveAuthorizationAsync(1, true, null), Times.Once);
        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Null(h.Vm.Notice);
        Assert.Empty(h.Vm.ResolveError);
    }

    [Fact]
    public async Task Reject_SendsTrimmedReason_AndApproveSendsNoReason()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Vm.Reason = "  Duplicado  ";
        h.Hub.Setup(x => x.ResolveAuthorizationAsync(1, false, "Duplicado"))
            .ReturnsAsync(new AuthorizationResolveInfo("Rejected", "Yo Mismo", "Duplicado"));

        await h.Vm.RejectCommand.ExecuteAsync(null);

        h.Hub.Verify(x => x.ResolveAuthorizationAsync(1, false, "Duplicado"), Times.Once);
        Assert.Equal(0, h.Vm.QueuedCount);

        h.RaiseRequested(BuildRequested(2));
        h.Hub.Setup(x => x.ResolveAuthorizationAsync(2, true, null))
            .ReturnsAsync(new AuthorizationResolveInfo("Approved", "Yo Mismo", null));

        await h.Vm.ApproveCommand.ExecuteAsync(null);

        h.Hub.Verify(x => x.ResolveAuthorizationAsync(2, true, null), Times.Once);
    }

    [Fact]
    public void ResolvedByOther_ClosesRequestWithExactRaceMessage()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.RaiseRequested(BuildRequested(2, cashier: "Cajero 02"));

        h.RaiseResolved(1, resolvedByName: "Admin Uno");

        Assert.Equal(new[] { 2 }, System.Linq.Enumerable.Select(h.Vm.PendingRequests, i => i.RequestId));
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Uno.", h.Vm.Notice);
        Assert.Equal(2, h.Vm.CurrentRequest!.RequestId);
    }

    [Fact]
    public void ResolvedEventForNeverQueuedRequest_IsIgnored()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));

        h.RaiseResolved(99, resolvedByName: "Admin Uno");

        Assert.Equal(1, h.Vm.QueuedCount);
        Assert.Null(h.Vm.Notice);
    }

    [Fact]
    public async Task OwnResolutionPush_AfterApprove_ClosesSilently()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.ResolveAuthorizationAsync(1, true, null))
            .ReturnsAsync(new AuthorizationResolveInfo("Approved", "Yo Mismo", null));

        await h.Vm.ApproveCommand.ExecuteAsync(null);
        h.RaiseResolved(1, resolvedByName: "Yo Mismo");

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Null(h.Vm.Notice);
    }

    [Fact]
    public async Task LostRaceResponse_PrefersServerMessageAndRemovesRequest()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.ResolveAuthorizationAsync(1, true, null))
            .ThrowsAsync(new AuthorizationHubException("Esta solicitud ya fue resuelta por Admin Dos.", 409));

        await h.Vm.ApproveCommand.ExecuteAsync(null);

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Dos.", h.Vm.Notice);
    }

    [Fact]
    public async Task LostRaceResponse_WithoutServerMessage_FallsBackToResolveFailed()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.ResolveAuthorizationAsync(1, true, null))
            .ThrowsAsync(new AuthorizationHubException(string.Empty, 404));

        await h.Vm.ApproveCommand.ExecuteAsync(null);

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Equal(AuthorizationNotificationViewModel.ResolveFailedMessage, h.Vm.Notice);
    }

    [Fact]
    public async Task ResolveTransportFailure_KeepsRequestAndSurfacesError()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.ResolveAuthorizationAsync(1, true, null))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await h.Vm.ApproveCommand.ExecuteAsync(null);

        Assert.Equal(1, h.Vm.QueuedCount);
        Assert.Equal("boom", h.Vm.ResolveError);
        Assert.Null(h.Vm.Notice);
    }

    // ── Cancelacion del cajero y reconciliacion en reconnect (W3) ──────────

    [Fact]
    public void CancelledResolutionPush_ClosesWithExactNoteNeverTheRaceMessage()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));

        h.RaiseResolvedStatus(1, "Cancelled", resolvedByName: "Admin Uno");

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Equal("Solicitud cancelada por el cajero.", h.Vm.Notice);
        Assert.Equal(AuthorizationNotificationViewModel.CancelledNoticeMessage, h.Vm.Notice);
        Assert.DoesNotContain("ya fue resuelta", h.Vm.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OwnResolutionPush_WithCancelledStatus_StillClosesSilently()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.ResolveAuthorizationAsync(1, true, null))
            .ReturnsAsync(new AuthorizationResolveInfo("Approved", "Yo Mismo", null));

        await h.Vm.ApproveCommand.ExecuteAsync(null);
        h.RaiseResolvedStatus(1, "Cancelled", resolvedByName: "Admin Dos");

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Null(h.Vm.Notice);
    }

    [Fact]
    public void Reconnected_ReconcilesResolvedQueuedRequestWithRaceNote()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.GetStatusAsync(1))
            .ReturnsAsync(new AuthorizationStatusInfo { Id = 1, Status = "Approved", ResolvedByName = "Admin Dos" });

        h.RaiseReconnected();

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Dos.", h.Vm.Notice);
    }

    [Fact]
    public void Reconnected_ReconcilesCancelledQueuedRequestWithExactNote()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.GetStatusAsync(1))
            .ReturnsAsync(new AuthorizationStatusInfo { Id = 1, Status = "Cancelled", ResolvedByName = "Admin Uno" });

        h.RaiseReconnected();

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Equal(AuthorizationNotificationViewModel.CancelledNoticeMessage, h.Vm.Notice);
    }

    [Fact]
    public void Reconnected_ReconcilesExpiredQueuedRequest()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.GetStatusAsync(1))
            .ReturnsAsync(new AuthorizationStatusInfo { Id = 1, Status = "Expired" });

        h.RaiseReconnected();

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Equal(AuthorizationNotificationViewModel.ExpiredNoticeMessage, h.Vm.Notice);
    }

    [Fact]
    public void Reconnected_NotFoundStatus_ClosesQueueAsExpired()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.GetStatusAsync(1)).ReturnsAsync((AuthorizationStatusInfo?)null);

        h.RaiseReconnected();

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Equal(AuthorizationNotificationViewModel.ExpiredNoticeMessage, h.Vm.Notice);
    }

    [Fact]
    public void Reconnected_StatusLookupFailure_KeepsQueueForNextReconnect()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.Hub.Setup(x => x.GetStatusAsync(1)).ThrowsAsync(new InvalidOperationException("socket caido"));

        h.RaiseReconnected();

        Assert.Equal(1, h.Vm.QueuedCount);
        Assert.Null(h.Vm.Notice);
    }

    [Fact]
    public void Reconnected_ReconcilesEveryQueuedRequestAndKeepsPendingOnes()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.RaiseRequested(BuildRequested(2, cashier: "Cajero 02"));
        h.RaiseRequested(BuildRequested(3, cashier: "Cajero 03"));
        h.Hub.Setup(x => x.GetStatusAsync(1))
            .ReturnsAsync(new AuthorizationStatusInfo { Id = 1, Status = "Approved", ResolvedByName = "Admin Dos" });
        h.Hub.Setup(x => x.GetStatusAsync(2))
            .ReturnsAsync(new AuthorizationStatusInfo { Id = 2, Status = "Pending", ExpiresAt = Start.AddSeconds(120) });
        h.Hub.Setup(x => x.GetStatusAsync(3))
            .ReturnsAsync(new AuthorizationStatusInfo { Id = 3, Status = "Cancelled" });

        h.RaiseReconnected();

        Assert.Equal(new[] { 2 }, System.Linq.Enumerable.Select(h.Vm.PendingRequests, i => i.RequestId));
        Assert.Equal(AuthorizationNotificationViewModel.CancelledNoticeMessage, h.Vm.Notice);
        h.Hub.Verify(x => x.GetStatusAsync(1), Times.Once);
        h.Hub.Verify(x => x.GetStatusAsync(2), Times.Once);
        h.Hub.Verify(x => x.GetStatusAsync(3), Times.Once);
    }

    // ── Expiracion ─────────────────────────────────────────────────────────

    [Fact]
    public void ExpiredEvent_RemovesRequestWithBriefNote()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));
        h.RaiseRequested(BuildRequested(2, cashier: "Cajero 02"));

        h.RaiseExpired(1);

        Assert.Equal(new[] { 2 }, System.Linq.Enumerable.Select(h.Vm.PendingRequests, i => i.RequestId));
        Assert.Equal(AuthorizationNotificationViewModel.ExpiredNoticeMessage, h.Vm.Notice);
    }

    [Fact]
    public void CountdownToZero_ExpiresHeadLocally_AndStopsTimerWhenEmpty()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1, expiresAt: Start.AddSeconds(60)));

        h.Now = Start.AddSeconds(61);
        h.Timer.RaiseTick();

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.False(h.Vm.HasPendingNotifications);
        Assert.Equal(AuthorizationNotificationViewModel.ExpiredNoticeMessage, h.Vm.Notice);
        Assert.True(h.Timer.StopCount >= 1);
        Assert.Equal(0, h.Vm.RemainingSeconds);
    }

    [Fact]
    public void Countdown_RefreshesForHeadAndNextRequestAfterRemoval()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1, expiresAt: Start.AddSeconds(30)));
        h.RaiseRequested(BuildRequested(2, cashier: "Cajero 02", expiresAt: Start.AddSeconds(90)));

        h.Now = Start.AddSeconds(10);
        h.Timer.RaiseTick();
        Assert.Equal("00:20", h.Vm.RemainingTimeDisplay);

        h.RaiseExpired(1);
        Assert.Equal("01:20", h.Vm.RemainingTimeDisplay);
    }

    [Fact]
    public void CountdownDisplay_FloorsPartialSecondInsteadOfCeiling()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1, expiresAt: Start.AddMilliseconds(59_900)));

        Assert.Equal(59, h.Vm.RemainingSeconds);
        Assert.Equal("00:59", h.Vm.RemainingTimeDisplay);
    }

    // ── Poda del set own-resolved (W3, R7/D4d) ─────────────────────────────

    [Fact]
    public void PruneOwnResolvedRequestIds_KeepsNewestEntriesWithinBound()
    {
        var ids = new System.Collections.Generic.List<int>();
        for (var id = 1; id <= AuthorizationNotificationViewModel.MaxOwnResolvedRequestIds + 5; id++)
        {
            ids.Add(id);
        }

        AuthorizationNotificationViewModel.PruneOwnResolvedRequestIds(ids);

        Assert.Equal(AuthorizationNotificationViewModel.MaxOwnResolvedRequestIds, ids.Count);
        Assert.Equal(6, ids[0]);
        Assert.Equal(AuthorizationNotificationViewModel.MaxOwnResolvedRequestIds + 5, ids[^1]);
    }

    [Fact]
    public void PruneOwnResolvedRequestIds_WithinBound_KeepsAllEntriesInOrder()
    {
        var ids = new System.Collections.Generic.List<int> { 4, 8, 15 };

        AuthorizationNotificationViewModel.PruneOwnResolvedRequestIds(ids);

        Assert.Equal(new[] { 4, 8, 15 }, ids);
    }

    // ── Ciclo de vida ──────────────────────────────────────────────────────

    [Fact]
    public void Deactivate_UnsubscribesAndClearsQueue()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));

        h.Vm.Deactivate();
        h.RaiseRequested(BuildRequested(2));

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.False(h.Vm.IsActive);

        h.Vm.Activate();
        h.RaiseRequested(BuildRequested(3));

        Assert.Equal(1, h.Vm.QueuedCount);
        Assert.Equal(3, h.Vm.CurrentRequest!.RequestId);
    }

    [Fact]
    public void Dispose_UnsubscribesFromHubAndDisposesTimer()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(1));

        h.Vm.Dispose();
        h.Vm.Dispose();

        Assert.True(h.Timer.Disposed);
        Assert.Equal(0, h.Vm.QueuedCount);

        h.RaiseRequested(BuildRequested(2));
        h.RaiseResolved(1);

        Assert.Equal(0, h.Vm.QueuedCount);
        Assert.Null(h.Vm.Notice);
    }

    // ── Orden aviso/cola (regresion del hallazgo T10) ──────────────────────

    [Fact]
    public void ResolvedByOther_SetsNoticeBeforeTheQueueDrainStateChange()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(11));

        var noticesSeenAtDrain = new System.Collections.Generic.List<string?>();
        h.Vm.StateChanged += (_, _) =>
        {
            if (!h.Vm.HasPendingNotifications)
            {
                noticesSeenAtDrain.Add(h.Vm.Notice);
            }
        };

        h.RaiseResolved(11, resolvedByName: "Admin Dos");

        Assert.NotEmpty(noticesSeenAtDrain);
        Assert.All(noticesSeenAtDrain, notice =>
            Assert.Equal("Esta solicitud ya fue resuelta por Admin Dos.", notice));
    }

    [Fact]
    public void Expired_SetsNoticeBeforeTheQueueDrainStateChange()
    {
        var h = new Harness();
        h.RaiseRequested(BuildRequested(21));

        var noticesSeenAtDrain = new System.Collections.Generic.List<string?>();
        h.Vm.StateChanged += (_, _) =>
        {
            if (!h.Vm.HasPendingNotifications)
            {
                noticesSeenAtDrain.Add(h.Vm.Notice);
            }
        };

        h.RaiseExpired(21);

        Assert.NotEmpty(noticesSeenAtDrain);
        Assert.All(noticesSeenAtDrain, notice =>
            Assert.Equal(AuthorizationNotificationViewModel.ExpiredNoticeMessage, notice));
    }
}
