using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Sales.Module.DTOs;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.150 (T9): maquina de estados del dialogo de espera del cajero. Determinista: sin timers
/// reales (IAuthorizationCountdownTimer fake) y sin red (hub mockeado).
/// </summary>
public class AuthorizationWaitViewModelTests
{
    private const int DefaultRequestId = 42;
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
        public Harness(int requestId = DefaultRequestId, DateTimeOffset? expiresAt = null, Func<string, Task>? retryAction = null)
        {
            Hub = new Mock<IAuthorizationHubService>();
            Timer = new FakeCountdownTimer();
            Hub.Setup(h => h.RequestAuthorizationAsync(It.IsAny<AuthorizationRequestContext>()))
                .ReturnsAsync(new AuthorizationRequestInfo(requestId, expiresAt ?? Start.AddSeconds(60)));
            Hub.Setup(h => h.GetStatusAsync(It.IsAny<int>()))
                .ReturnsAsync((AuthorizationStatusInfo?)null);

            Vm = new AuthorizationWaitViewModel(
                Hub.Object,
                Context,
                retryAction ?? (token =>
                {
                    RetriedTokens.Add(token);
                    return Task.CompletedTask;
                }),
                new InlineDispatcherInvoker(),
                Timer,
                () => Now);
            Vm.RequestClose += result => CloseResults.Add(result);
        }

        public Mock<IAuthorizationHubService> Hub { get; }
        public FakeCountdownTimer Timer { get; }
        public AuthorizationWaitViewModel Vm { get; }
        public List<string> RetriedTokens { get; } = new();
        public List<bool> CloseResults { get; } = new();
        public DateTimeOffset Now { get; set; } = Start;

        public static AuthorizationRequestContext Context => new()
        {
            SaleId = 77,
            ProductId = 5,
            ProductName = "Café 1kg",
            Quantity = 2m,
            CustomUnitPriceUsd = 3.5m,
            CustomUnitPriceLocal = 35m,
            Terminal = "Caja-01"
        };

        public void RaiseResolved(int requestId, bool approved, string? token = null, string? reason = null, string? resolvedByName = "Admin Uno", string? status = null, string? resolutionMode = "Remote")
        {
            Hub.Raise(
                h => h.AuthorizationResolved += null,
                new AuthorizationResolvedPayload
                {
                    RequestId = requestId,
                    Status = status ?? (approved ? "Approved" : "Rejected"),
                    Approved = approved,
                    Token = token,
                    Reason = reason,
                    ResolvedByName = resolvedByName,
                    ResolutionMode = approved ? resolutionMode : null
                });
        }

        public void RaiseExpired(int requestId)
        {
            Hub.Raise(
                h => h.AuthorizationExpired += null,
                new AuthorizationExpiredPayload { RequestId = requestId, Status = "Expired" });
        }
    }

    // ── Bloqueo activo y countdown ─────────────────────────────────────────

    [Fact]
    public async Task Start_EntersWaitingStateWithExactMessageAndCountdown()
    {
        var h = new Harness(expiresAt: Start.AddSeconds(60));

        await h.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(AuthorizationWaitPhase.Waiting, h.Vm.Phase);
        Assert.Equal("Esperando autorización remota...", h.Vm.StatusMessage);
        Assert.Equal("Esperando autorización remota...", AuthorizationWaitViewModel.WaitingMessage);
        Assert.Equal(60, h.Vm.RemainingSeconds);
        Assert.Equal("01:00", h.Vm.RemainingTimeDisplay);
        Assert.True(h.Vm.ShowWaitingPanel);
        Assert.Equal(1, h.Timer.StartCount);
        Assert.Equal(TimeSpan.FromMilliseconds(500), h.Timer.LastInterval);
        h.Hub.Verify(x => x.RequestAuthorizationAsync(It.IsAny<AuthorizationRequestContext>()), Times.Once);
    }

    [Fact]
    public async Task Countdown_TicksFromInjectedClock_AndExpiresAtZero()
    {
        var h = new Harness(expiresAt: Start.AddSeconds(60));
        await h.Vm.StartCommand.ExecuteAsync(null);

        h.Now = Start.AddSeconds(30);
        h.Timer.RaiseTick();

        Assert.Equal(30, h.Vm.RemainingSeconds);
        Assert.Equal("00:30", h.Vm.RemainingTimeDisplay);

        h.Now = Start.AddSeconds(61);
        h.Timer.RaiseTick();

        Assert.Equal(0, h.Vm.RemainingSeconds);
        Assert.Equal(AuthorizationWaitPhase.Expired, h.Vm.Phase);
        Assert.Equal("Expirada", h.Vm.StatusMessage);
        Assert.True(h.Timer.StopCount >= 1);
    }

    // ── Aprobacion con token ──────────────────────────────────────────────

    [Fact]
    public async Task ApprovedResolution_InvokesRetryOnceWithTokenAndClosesOnSuccess()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);

        h.RaiseResolved(DefaultRequestId, approved: true, token: "tok-1");
        h.RaiseResolved(DefaultRequestId, approved: true, token: "tok-1");

        Assert.Equal(new[] { "tok-1" }, h.RetriedTokens);
        Assert.Equal(new[] { true }, h.CloseResults);
        Assert.Equal(AuthorizationWaitPhase.Granted, h.Vm.Phase);
        Assert.True(h.Timer.StopCount >= 1);
    }

    [Fact]
    public async Task ApprovedWithoutToken_DegradesToExpiredWithoutRetry()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);

        h.RaiseResolved(DefaultRequestId, approved: true, token: null);

        Assert.Equal(AuthorizationWaitPhase.Expired, h.Vm.Phase);
        Assert.Empty(h.RetriedTokens);
        Assert.Empty(h.CloseResults);
    }

    [Fact]
    public async Task ResolutionsForOtherRequests_AreIgnored()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);

        h.RaiseResolved(DefaultRequestId + 1, approved: true, token: "tok-otro");
        h.RaiseExpired(DefaultRequestId + 1);

        Assert.Equal(AuthorizationWaitPhase.Waiting, h.Vm.Phase);
        Assert.Empty(h.RetriedTokens);
    }

    // ── Rechazo y expiracion ──────────────────────────────────────────────

    [Fact]
    public async Task RejectedResolution_ShowsExactMessageWithReasonAndUnlocks()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);

        h.RaiseResolved(DefaultRequestId, approved: false, reason: "Monto fuera de política");

        Assert.Equal(AuthorizationWaitPhase.Rejected, h.Vm.Phase);
        Assert.Equal("Solicitud rechazada.", h.Vm.StatusMessage);
        Assert.Equal("Monto fuera de política", h.Vm.RejectionReason);
        Assert.Empty(h.RetriedTokens);
        Assert.True(h.Timer.StopCount >= 1);
    }

    [Fact]
    public async Task ExpiryEvent_SwitchesToExpiredAndRetryStartsNewRequest()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);

        h.RaiseExpired(DefaultRequestId);

        Assert.Equal(AuthorizationWaitPhase.Expired, h.Vm.Phase);
        Assert.Equal("Expirada", h.Vm.StatusMessage);

        h.Hub.SetupSequence(x => x.RequestAuthorizationAsync(It.IsAny<AuthorizationRequestContext>()))
            .ReturnsAsync(new AuthorizationRequestInfo(43, Start.AddSeconds(120)));

        await h.Vm.RetryCommand.ExecuteAsync(null);

        Assert.Equal(AuthorizationWaitPhase.Waiting, h.Vm.Phase);
        Assert.Equal(120, h.Vm.RemainingSeconds);
        Assert.Equal(2, h.Timer.StartCount);
        h.Hub.Verify(x => x.RequestAuthorizationAsync(It.IsAny<AuthorizationRequestContext>()), Times.Exactly(2));
    }

    // ── Autorizacion local ────────────────────────────────────────────────

    [Fact]
    public async Task LocalAuthorization_InvalidCredentials_ShowsExactMessageAndKeepsWaiting()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);
        h.Hub.Setup(x => x.LocalResolveAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new AuthorizationHubException(AuthorizationMessages.InvalidCredentials, 400));

        h.Vm.OpenLocalAuthorizationCommand.Execute(null);
        h.Vm.LocalUsername = "supervisor";
        h.Vm.LocalPassword = "clave-mala";
        await h.Vm.SubmitLocalAuthorizationCommand.ExecuteAsync(null);

        Assert.Equal("Credenciales inválidas o sin privilegios para autorizar.", h.Vm.LocalError);
        Assert.Equal(AuthorizationMessages.InvalidCredentials, h.Vm.LocalError);
        Assert.Equal(AuthorizationWaitPhase.Waiting, h.Vm.Phase);
        Assert.True(h.Vm.IsLocalFormOpen);
        Assert.Empty(h.RetriedTokens);
    }

    [Fact]
    public async Task LocalAuthorization_RepeatedFailures_KeepTheGenericNonDisclosingMessage()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);
        h.Hub.Setup(x => x.LocalResolveAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new AuthorizationHubException(AuthorizationMessages.InvalidCredentials, 400));

        h.Vm.OpenLocalAuthorizationCommand.Execute(null);
        h.Vm.LocalUsername = "supervisor";
        h.Vm.LocalPassword = "clave-mala";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await h.Vm.SubmitLocalAuthorizationCommand.ExecuteAsync(null);
        }

        Assert.Equal("Credenciales inválidas o sin privilegios para autorizar.", h.Vm.LocalError);
        Assert.DoesNotContain("bloque", h.Vm.LocalError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AuthorizationWaitPhase.Waiting, h.Vm.Phase);
        Assert.True(h.Vm.IsLocalFormOpen);
    }

    [Fact]
    public async Task LocalAuthorization_Success_RetriesWithReturnedTokenAndCloses()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);
        h.Hub.Setup(x => x.LocalResolveAsync(DefaultRequestId, "supervisor", "clave-buena", null))
            .ReturnsAsync(new LocalResolveInfo("local-tok", 9, "Supervisora", "Approved"));

        h.Vm.OpenLocalAuthorizationCommand.Execute(null);
        h.Vm.LocalUsername = " supervisor ";
        h.Vm.LocalPassword = "clave-buena";
        await h.Vm.SubmitLocalAuthorizationCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "local-tok" }, h.RetriedTokens);
        Assert.Equal(new[] { true }, h.CloseResults);
        Assert.False(h.Vm.IsLocalFormOpen);
        Assert.Equal(string.Empty, h.Vm.LocalPassword);
        Assert.Equal(string.Empty, h.Vm.LocalError);
    }

    [Fact]
    public async Task LocalAuthorization_AlreadyResolved_RecoversStatusAndUsesDeliveredToken()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);
        h.Hub.Setup(x => x.LocalResolveAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new AuthorizationHubException("Esta solicitud ya fue resuelta por Admin Uno.", 409));
        h.Hub.Setup(x => x.GetStatusAsync(DefaultRequestId))
            .ReturnsAsync(new AuthorizationStatusInfo
            {
                Id = DefaultRequestId,
                Status = "Approved",
                ResolutionMode = "Remote",
                ResolvedByName = "Admin Uno",
                Token = "recovered-tok"
            });

        h.Vm.OpenLocalAuthorizationCommand.Execute(null);
        h.Vm.LocalUsername = "supervisor";
        h.Vm.LocalPassword = "clave";
        await h.Vm.SubmitLocalAuthorizationCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "recovered-tok" }, h.RetriedTokens);
        Assert.Equal(string.Empty, h.Vm.LocalError);
        Assert.Equal(AuthorizationWaitPhase.Granted, h.Vm.Phase);
    }

    [Fact]
    public async Task LocalAuthorization_EmptyCredentials_ShowsExactMessageWithoutCallingService()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);
        h.Vm.OpenLocalAuthorizationCommand.Execute(null);
        h.Vm.LocalUsername = "   ";
        h.Vm.LocalPassword = "clave";

        await h.Vm.SubmitLocalAuthorizationCommand.ExecuteAsync(null);

        Assert.Equal("Credenciales inválidas o sin privilegios para autorizar.", h.Vm.LocalError);
        h.Hub.Verify(x => x.LocalResolveAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        Assert.Equal(AuthorizationWaitPhase.Waiting, h.Vm.Phase);
    }

    // ── Reintento de la ejecucion protegida ───────────────────────────────

    [Fact]
    public async Task FailedRetry_ShowsErrorAndManualRetryReusesTheSameToken()
    {
        var calls = new List<string>();
        var firstAttempt = true;
        var h = new Harness(retryAction: token =>
        {
            calls.Add(token);
            if (firstAttempt)
            {
                firstAttempt = false;
                return Task.FromException(new InvalidOperationException("Token ya consumido."));
            }

            return Task.CompletedTask;
        });
        await h.Vm.StartCommand.ExecuteAsync(null);

        h.RaiseResolved(DefaultRequestId, approved: true, token: "tok-x");

        Assert.Equal("Token ya consumido.", h.Vm.RetryError);
        Assert.True(h.Vm.ShowRetryExecutionPanel);
        Assert.True(h.Vm.RetryExecutionCommand.CanExecute(null));
        Assert.Empty(h.CloseResults);

        await h.Vm.RetryExecutionCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "tok-x", "tok-x" }, calls);
        Assert.Equal(new[] { true }, h.CloseResults);
    }

    // ── Cancelar y ciclo de vida ──────────────────────────────────────────

    [Fact]
    public async Task Cancel_ClosesWithFalseAndIgnoresLateResolutions()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);

        h.Vm.CancelCommand.Execute(null);

        Assert.Equal(new[] { false }, h.CloseResults);
        Assert.Equal(AuthorizationWaitPhase.Idle, h.Vm.Phase);
        Assert.True(h.Timer.StopCount >= 1);

        h.RaiseResolved(DefaultRequestId, approved: true, token: "late");
        Assert.Empty(h.RetriedTokens);
    }

    [Fact]
    public async Task Dispose_StopsCountdownUnsubscribesAndIsIdempotent()
    {
        var h = new Harness();
        await h.Vm.StartCommand.ExecuteAsync(null);

        h.Vm.Dispose();
        h.Vm.Dispose();

        Assert.True(h.Timer.Disposed);
        h.Hub.VerifyRemove(
            x => x.AuthorizationResolved -= It.IsAny<EventHandler<AuthorizationResolvedPayload>>(),
            Times.Once);
        h.Hub.VerifyRemove(
            x => x.AuthorizationExpired -= It.IsAny<EventHandler<AuthorizationExpiredPayload>>(),
            Times.Once);

        h.RaiseResolved(DefaultRequestId, approved: true, token: "late");
        Assert.Empty(h.RetriedTokens);
        Assert.Equal(AuthorizationWaitPhase.Waiting, h.Vm.Phase);
    }

    [Fact]
    public async Task Dispose_BeforeStart_DoesNotThrowAndBlocksStart()
    {
        var h = new Harness();

        h.Vm.Dispose();
        await h.Vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(AuthorizationWaitPhase.Idle, h.Vm.Phase);
        h.Hub.Verify(x => x.RequestAuthorizationAsync(It.IsAny<AuthorizationRequestContext>()), Times.Never);
    }

    // ── Contexto de la operacion ──────────────────────────────────────────

    [Fact]
    public void OperationSummary_IncludesProductSaleAndQuantity()
    {
        var h = new Harness();

        Assert.Contains("Café 1kg", h.Vm.OperationSummary);
        Assert.Contains("#77", h.Vm.OperationSummary);
        Assert.Equal("#77", h.Vm.SaleNumberDisplay);
        Assert.Equal("Café 1kg × 2", h.Vm.ProductDisplay);
        Assert.Equal("$ 3.50", h.Vm.CustomPriceUsdDisplay);
        Assert.Equal("Bs.S 35.00", h.Vm.CustomPriceLocalDisplay);
    }

    [Fact]
    public void OperationSummary_WithoutProductName_FallsBackToGenericLabel()
    {
        var context = new AuthorizationRequestContext { SaleId = 5, ProductId = 1, Quantity = 1m };

        var summary = AuthorizationWaitViewModel.BuildOperationSummary(context);

        Assert.Contains("Producto", summary);
        Assert.Contains("#5", summary);
    }
}
