using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3c3c1 (S4/S6): in Dual mode the local drawer currency is the normalized secondary.
/// VES keeps the historical Bs.S label; a 2dp ISO secondary (COP) is snapshotted as-is.
/// </summary>
public class DualSecondaryCurrencyCashDrawerTests
{
    private static Mock<IMonetaryProfileService> CreateDualProfileMock(string secondaryCode = "COP")
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = secondaryCode,
                SecondaryCurrencySymbol = secondaryCode == "VES" ? "Bs.S" : secondaryCode,
                IsConfigured = true
            });
        return profile;
    }

    [Fact]
    public async Task DualCop_OpeningClosingAndLocalTransactions_SnapshotCopAndKeepAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context, CreateDualProfileMock().Object);

        var opened = await service.OpenSessionAsync(1000m, 4000m);
        var cashIn = await service.AddTransactionAsync(
            opened.Id, CashTransactionType.Income, CashTransactionSource.CashIn, 100m, 0.025m, 4000m, "Ingreso local");
        var recordedChange = await service.RecordSaleChangeAsync(
            opened.Id, changeUsd: 0.005m, changeBsS: 20m, exchangeRate: 4000m, "Vuelto", saleId: 1, cashPaymentMethodId: null);
        var closed = await service.CloseSessionAsync(1000m, 4000m);

        var persisted = await context.CashTransactions.AsNoTracking()
            .OrderBy(t => t.Id)
            .ToListAsync();

        Assert.Equal(4, persisted.Count);
        Assert.Equal(new[] { "COP", "COP", "COP", "COP" }, persisted.Select(t => t.CurrencyCode).ToArray());
        Assert.Equal(new[] { 1000m, 100m, 20m, 1000m }, persisted.Select(t => t.AmountLocal).ToArray());
        Assert.Equal("COP", cashIn.CurrencyCode);
        Assert.Equal("COP", recordedChange.CurrencyCode);
        Assert.Equal(1000m, closed.ClosingBalanceLocal);
    }

    [Fact]
    public async Task DualCop_LocalBalance_TracksCopMovements()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context, CreateDualProfileMock().Object);
        var session = await service.OpenSessionAsync(1000m, 4000m);

        await service.AddTransactionAsync(
            session.Id, CashTransactionType.Income, CashTransactionSource.CashIn, 100m, 0.025m, 4000m, "Ingreso local");
        await service.RecordSaleChangeAsync(
            session.Id, changeUsd: 0.005m, changeBsS: 20m, exchangeRate: 4000m, "Vuelto", saleId: 1, cashPaymentMethodId: null);
        // RecordSaleChangeAsync participates in the caller's transaction; persist before reading.
        await context.SaveChangesAsync();

        Assert.Equal(1080m, await service.GetCurrentBalanceLocalAsync(session.Id));
        Assert.Equal(1080m, await service.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));
    }

    [Fact]
    public async Task DualCop_InsufficientChangeMessage_UsesResolvedLocalCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context, CreateDualProfileMock().Object);
        var session = await service.OpenSessionAsync(10m, 4000m);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RecordSaleChangeAsync(
                session.Id, changeUsd: 0.005m, changeBsS: 100m, exchangeRate: 4000m, "Vuelto", saleId: 1, cashPaymentMethodId: null));

        Assert.Contains("Vuelto requerido", exception.Message);
        Assert.Contains("COP", exception.Message);
        Assert.DoesNotContain("Bs.S", exception.Message);
    }
}
