using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Wpf.E2ETests.Fixtures;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// 8.212-WU6: full-stack evidence for the physical-method drawer. Opens a session with an explicit
/// method breakdown, reads the active session's per-method balances, processes a method-aware cash
/// advance against the real backend/PostgreSQL stack, and verifies the resulting balances.
/// Gateado por <c>E2E_POSTGRES_CONNECTION</c>, igual que el resto del harness full-stack.
/// </summary>
public class FullStackPhysicalMethodDrawerTests : IClassFixture<FullStackFixture>
{
    private const int UsdMethodId = 1;
    private const int LocalMethodId = 2;
    private const int ReceiverMethodId = 4;

    private readonly FullStackFixture _fixture;

    public FullStackPhysicalMethodDrawerTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OpenWithMethods_AndAdvance_ReflectsPerMethodBalances()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        await _fixture.InitializeAsync();

        try
        {
            var bootstrap = _fixture.Bootstrap
                ?? throw new InvalidOperationException("El bootstrap del harness no produjo estado inicial.");
            var token = _fixture.AdminToken
                ?? throw new InvalidOperationException("El bootstrap no produjo un token de Admin.");

            using var api = new E2eApiClient(_fixture.BackendBaseAddress);
            var sessionId = await api.OpenCashDrawerSessionWithMethodsAsync(
                token,
                openingBalanceLocal: 1000m,
                openingBalanceUsd: 20m,
                exchangeRate: bootstrap.ExchangeRate,
                methodBalances: new Dictionary<int, decimal>
                {
                    [UsdMethodId] = 20m,
                    [LocalMethodId] = 1000m
                });

            var active = await api.GetActiveCashDrawerSessionAsync(token);
            Assert.NotNull(active);
            Assert.Equal(sessionId, active!.Id);
            Assert.Equal(2, active.PhysicalMethodBalances.Count);

            var initialLocal = Assert.Single(
                active.PhysicalMethodBalances,
                balance => balance.PaymentMethodId == LocalMethodId);
            var initialUsd = Assert.Single(
                active.PhysicalMethodBalances,
                balance => balance.PaymentMethodId == UsdMethodId);
            Assert.Equal(1000m, initialLocal.CurrentBalance);
            Assert.Equal(20m, initialUsd.CurrentBalance);
            Assert.True(initialLocal.CanDispense);

            await api.ProcessCashAdvanceWithMethodAsync(
                token,
                sessionId,
                dispensedAmount: 100m,
                dispensedMethodId: LocalMethodId,
                receivedMethodId: ReceiverMethodId,
                exchangeRate: bootstrap.ExchangeRate);

            var after = await api.GetPhysicalMethodBalancesAsync(token, sessionId);
            var finalLocal = Assert.Single(after, balance => balance.PaymentMethodId == LocalMethodId);
            var finalUsd = Assert.Single(after, balance => balance.PaymentMethodId == UsdMethodId);
            Assert.Equal(900m, finalLocal.CurrentBalance);
            Assert.Equal(20m, finalUsd.CurrentBalance);
        }
        finally
        {
            await _fixture.TeardownAsync();
        }
    }
}
