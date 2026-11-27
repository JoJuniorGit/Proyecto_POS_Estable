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
            var methods = await api.GetActivePaymentMethodsAsync(token);
            var localMethod = methods.FirstOrDefault(method => method.IsCash && IsLocalCurrency(method.Currency))
                ?? throw new InvalidOperationException(
                    $"No hay un método físico local activo. Métodos: {DescribeMethods(methods)}");
            var usdMethod = methods.FirstOrDefault(method =>
                    method.IsCash && string.Equals(method.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"No hay un método físico USD activo. Métodos: {DescribeMethods(methods)}");
            var receiverMethod = methods.FirstOrDefault(method => !method.IsCash)
                ?? throw new InvalidOperationException(
                    $"No hay un método digital receptor activo. Métodos: {DescribeMethods(methods)}");

            var sessionId = await api.OpenCashDrawerSessionWithMethodsAsync(
                token,
                openingBalanceLocal: 1000m,
                openingBalanceUsd: 20m,
                exchangeRate: bootstrap.ExchangeRate,
                methodBalances: new Dictionary<int, decimal>
                {
                    [usdMethod.Id] = 20m,
                    [localMethod.Id] = 1000m
                });

            var active = await api.GetActiveCashDrawerSessionAsync(token);
            Assert.NotNull(active);
            Assert.Equal(sessionId, active!.Id);
            Assert.Equal(2, active.PhysicalMethodBalances.Count);

            var initialLocal = Assert.Single(
                active.PhysicalMethodBalances,
                balance => balance.PaymentMethodId == localMethod.Id);
            var initialUsd = Assert.Single(
                active.PhysicalMethodBalances,
                balance => balance.PaymentMethodId == usdMethod.Id);
            Assert.Equal(1000m, initialLocal.CurrentBalance);
            Assert.Equal(20m, initialUsd.CurrentBalance);
            Assert.True(initialLocal.CanDispense);

            await api.ProcessCashAdvanceWithMethodAsync(
                token,
                sessionId,
                dispensedAmount: 100m,
                dispensedMethodId: localMethod.Id,
                receivedMethodId: receiverMethod.Id,
                exchangeRate: bootstrap.ExchangeRate);

            var after = await api.GetPhysicalMethodBalancesAsync(token, sessionId);
            var finalLocal = Assert.Single(after, balance => balance.PaymentMethodId == localMethod.Id);
            var finalUsd = Assert.Single(after, balance => balance.PaymentMethodId == usdMethod.Id);
            Assert.Equal(900m, finalLocal.CurrentBalance);
            Assert.Equal(20m, finalUsd.CurrentBalance);
        }
        finally
        {
            await _fixture.TeardownAsync();
        }
    }

    private static bool IsLocalCurrency(string currency) =>
        string.Equals(currency, "Bs.S", StringComparison.OrdinalIgnoreCase)
        || string.Equals(currency, "VES", StringComparison.OrdinalIgnoreCase)
        || string.Equals(currency, "BsS", StringComparison.OrdinalIgnoreCase);

    private static string DescribeMethods(IReadOnlyList<E2ePaymentMethod> methods) =>
        string.Join(", ", methods.Select(method =>
            $"{method.Id}:{method.Name}:{method.Currency}:cash={method.IsCash}"));
}
