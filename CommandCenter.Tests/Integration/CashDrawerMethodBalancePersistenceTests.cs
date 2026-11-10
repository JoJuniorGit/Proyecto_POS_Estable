using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.212-WU1: the session opens with one physical method balance row per active physical
/// method, auto-assigns legacy local/USD openings only when the currency has a single method,
/// and carries those rows through close/rollover.
/// </summary>
public class CashDrawerMethodBalancePersistenceTests
{
    private static Mock<IMonetaryProfileService> DualProfile()
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = "VES",
            SecondaryCurrencySymbol = "Bs.S",
            IsConfigured = true
        });
        return profile;
    }

    [Fact]
    public async Task OpenSession_CreatesOneRowPerPhysicalMethod_AndAutoAssignsLegacyOpening()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var service = new CashDrawerService(context, DualProfile().Object);

        var session = await service.OpenSessionAsync(100m, 50m, openingBalanceUsd: 20m);

        var rows = await context.CashDrawerSessionMethodBalances
            .AsNoTracking()
            .Where(row => row.CashDrawerSessionId == session.Id)
            .OrderBy(row => row.PaymentMethodId)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal("USD", rows[0].CurrencyCode);
        Assert.Equal(20m, rows[0].OpeningAmount);
        Assert.Equal("VES", rows[1].CurrencyCode);
        Assert.Equal(100m, rows[1].OpeningAmount);
        Assert.Equal(100m, session.OpeningBalanceLocal);
        Assert.Equal(20m, session.OpeningBalanceUsd);
    }

    [Fact]
    public async Task OpenSession_WithTwoMethodsForLocalCurrency_WithoutBreakdown_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 60,
            Name = "Efectivo Bs.S secundario",
            IsCash = true,
            IsActive = true,
            Currency = "Bs.S",
            DisplayOrder = 6
        });
        await context.SaveChangesAsync();

        var service = new CashDrawerService(context, DualProfile().Object);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.OpenSessionAsync(100m, 50m, openingBalanceUsd: 20m));

        Assert.Contains("desglose", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await context.CashDrawerSessions.AnyAsync());
    }

    [Fact]
    public async Task OpenSession_WithExplicitBreakdown_SplitsTheLocalOpeningAndKeepsAggregates()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 60,
            Name = "Efectivo Bs.S secundario",
            IsCash = true,
            IsActive = true,
            Currency = "Bs.S",
            DisplayOrder = 6
        });
        await context.SaveChangesAsync();

        var service = new CashDrawerService(context, DualProfile().Object);
        var session = await service.OpenSessionAsync(
            100m,
            50m,
            openingBalanceUsd: 20m,
            requestedMethodBalances: new List<OpenSessionMethodBalanceRequest>
            {
                new() { PaymentMethodId = 2, OpeningAmount = 60m },
                new() { PaymentMethodId = 60, OpeningAmount = 40m }
            });

        var rows = await context.CashDrawerSessionMethodBalances
            .AsNoTracking()
            .Where(row => row.CashDrawerSessionId == session.Id)
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Equal(60m, rows.Single(row => row.PaymentMethodId == 2).OpeningAmount);
        Assert.Equal(40m, rows.Single(row => row.PaymentMethodId == 60).OpeningAmount);
        Assert.Equal(100m, session.OpeningBalanceLocal);
        Assert.Equal(20m, session.OpeningBalanceUsd);
    }

    [Fact]
    public async Task CloseAndRollover_PreservePerMethodOpeningAndClosingAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var service = new CashDrawerService(context, DualProfile().Object);
        var session = await service.OpenSessionAsync(100m, 50m, openingBalanceUsd: 20m);

        await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.CashIn,
            10m,
            0.2m,
            50m,
            "Ingreso físico Bs.S",
            isPhysicalCash: true,
            paymentMethodId: 2);

        await service.RolloverSessionAfterClosureAsync(50m);
        var closedRows = await context.CashDrawerSessionMethodBalances
            .AsNoTracking()
            .Where(row => row.CashDrawerSessionId == session.Id)
            .ToListAsync();
        Assert.Equal(110m, closedRows.Single(row => row.PaymentMethodId == 2).ClosingAmount);
        Assert.Equal(20m, closedRows.Single(row => row.PaymentMethodId == 1).ClosingAmount);
        var reopened = await context.CashDrawerSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Status == CashDrawerStatus.Open);
        var reopenedRows = await context.CashDrawerSessionMethodBalances
            .AsNoTracking()
            .Where(row => row.CashDrawerSessionId == reopened.Id)
            .ToListAsync();

        Assert.Equal(110m, reopenedRows.Single(row => row.PaymentMethodId == 2).OpeningAmount);
        Assert.Equal(20m, reopenedRows.Single(row => row.PaymentMethodId == 1).OpeningAmount);
    }
}
