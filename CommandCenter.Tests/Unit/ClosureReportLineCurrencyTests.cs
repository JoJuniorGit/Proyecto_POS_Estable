using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.TestHelpers;
using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Sales.Module;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ClosureReportLineCurrencyTests
{
    private const decimal ExchangeRate = 50m;

    private static SalesDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private static async Task SeedMethodAsync(SalesDbContext context, int id, string name, bool isCash, CancellationToken cancellationToken = default)
    {
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = id,
            Name = name,
            IsCash = isCash,
            IsActive = true,
            IsDeleted = false,
            DisplayOrder = id
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedSaleAsync(SalesDbContext context, int saleId, int paymentMethodId, decimal amountBsS, CancellationToken cancellationToken = default)
    {
        context.Sales.Add(new Sale
        {
            Id = saleId,
            Status = SaleStatus.Completed,
            Date = DateTime.UtcNow,
            TotalUSD = amountBsS / ExchangeRate,
            TotalBsS = amountBsS,
            AppliedRate = ExchangeRate
        });
        context.SalePayments.Add(new SalePayment
        {
            SaleId = saleId,
            PaymentMethodId = paymentMethodId,
            Amount = amountBsS / ExchangeRate,
            AmountBsS = amountBsS,
            ExchangeRate = ExchangeRate
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static CreateClosureCommand CreateCommand(params DeclaredPaymentAmount[] declarations)
    {
        return new CreateClosureCommand(
            ClosureDateUtc: DateTime.UtcNow,
            UserId: "Admin",
            Observation: "V-00000000",
            Declarations: declarations.ToList());
    }

    [Fact]
    public async Task MergedUsdLine_ExpressesDeclaredSystemAndDifferenceInUsd()
    {
        using var context = CreateInMemoryContext();
        await SeedMethodAsync(context, 1, "Pago Movil USD", isCash: false);
        await SeedSaleAsync(context, 1, 1, 2500m);

        var service = DailyClosureTestHelper.CreateService(context);
        var result = await service.CreateClosureFromCommandAsync(CreateCommand(), CancellationToken.None);

        var detail = result.Details.Single(d => d.PaymentMethodId == 1);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, detail.Currency);
        Assert.Equal(PricingCalculator.ToUSD(2500m, ExchangeRate), detail.SystemAmount);
        Assert.Equal(PricingCalculator.ToUSD(2500m, ExchangeRate), detail.DeclaredAmount);
        Assert.Equal(0m, detail.Difference);
        Assert.NotEqual(2500m, detail.DeclaredAmount);
        Assert.Equal(ClosureStatus.Balanced, detail.Status);
    }

    [Fact]
    public async Task MergedUndeclaredCashMethod_NonZeroDifference_IsShortageNeverBalanced()
    {
        using var context = CreateInMemoryContext();
        await SeedMethodAsync(context, 2, "Efectivo Bs.S", isCash: true);
        await SeedSaleAsync(context, 2, 2, 1000m);

        var service = DailyClosureTestHelper.CreateService(context);
        var result = await service.CreateClosureFromCommandAsync(CreateCommand(), CancellationToken.None);

        var detail = result.Details.Single(d => d.PaymentMethodId == 2);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, detail.Currency);
        Assert.Equal(0m, detail.DeclaredAmount);
        Assert.Equal(1000m, detail.SystemAmount);
        Assert.Equal(-1000m, detail.Difference);
        Assert.Equal(ClosureStatus.Shortage, detail.Status);
        Assert.NotEqual(ClosureStatus.Balanced, detail.Status);
    }

    [Fact]
    public async Task MergedUsdCashMethod_NonZeroDifference_IsShortageNeverBalanced()
    {
        using var context = CreateInMemoryContext();
        await SeedMethodAsync(context, 3, "Efectivo USD", isCash: true);
        await SeedSaleAsync(context, 3, 3, 2500m);

        var service = DailyClosureTestHelper.CreateService(context);
        var result = await service.CreateClosureFromCommandAsync(CreateCommand(), CancellationToken.None);

        var detail = result.Details.Single(d => d.PaymentMethodId == 3);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, detail.Currency);
        Assert.Equal(0m, detail.DeclaredAmount);
        Assert.Equal(50m, detail.SystemAmount);
        Assert.Equal(-50m, detail.Difference);
        Assert.Equal(ClosureStatus.Shortage, detail.Status);
        Assert.NotEqual(ClosureStatus.Balanced, detail.Status);
    }

    [Fact]
    public async Task MergedLocalLine_WithinTolerance_StaysBalanced()
    {
        using var context = CreateInMemoryContext();
        await SeedMethodAsync(context, 4, "Punto de Venta", isCash: false);
        await SeedSaleAsync(context, 4, 4, 1000m);

        var service = DailyClosureTestHelper.CreateService(context);
        var result = await service.CreateClosureFromCommandAsync(CreateCommand(), CancellationToken.None);

        var detail = result.Details.Single(d => d.PaymentMethodId == 4);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, detail.Currency);
        Assert.Equal(1000m, detail.DeclaredAmount);
        Assert.Equal(1000m, detail.SystemAmount);
        Assert.Equal(0m, detail.Difference);
        Assert.Equal(ClosureStatus.Balanced, detail.Status);
    }

    [Fact]
    public async Task DeclaredUsdLine_PerCurrencyDifference_IsSurplus()
    {
        using var context = CreateInMemoryContext();
        await SeedMethodAsync(context, 5, "Efectivo USD", isCash: true);
        await SeedSaleAsync(context, 5, 5, 2500m);

        var service = DailyClosureTestHelper.CreateService(context);
        var result = await service.CreateClosureFromCommandAsync(
            CreateCommand(new DeclaredPaymentAmount(5, 60m)), CancellationToken.None);

        var detail = result.Details.Single(d => d.PaymentMethodId == 5);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, detail.Currency);
        Assert.Equal(60m, detail.DeclaredAmount);
        Assert.Equal(50m, detail.SystemAmount);
        Assert.Equal(10m, detail.Difference);
        Assert.Equal(ClosureStatus.Surplus, detail.Status);
    }

    [Fact]
    public async Task PersistedClosureSnapshot_IsNotRewrittenByLaterClosureCommand()
    {
        using var context = CreateInMemoryContext();
        await SeedMethodAsync(context, 6, "Efectivo USD", isCash: true);
        await SeedSaleAsync(context, 6, 6, 2500m);

        context.DailyClosures.Add(new DailyClosure
        {
            Id = 10,
            ClosureDate = DateTime.UtcNow.AddDays(-1),
            UserId = "Admin",
            ExchangeRate = ExchangeRate,
            TotalExpectedBsS = 1234.56m,
            TotalActualBsS = 1200.00m,
            TotalDifferenceBsS = -34.56m,
            Details = new List<ClosureDetail>
            {
                new ClosureDetail
                {
                    PaymentMethodId = 6,
                    PaymentMethodName = "Efectivo USD",
                    ExpectedAmountBsS = 1234.56m,
                    ActualAmountBsS = 1200.00m,
                    DifferenceBsS = -34.56m
                }
            }
        });
        await context.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(context);
        var result = await service.CreateClosureFromCommandAsync(
            CreateCommand(new DeclaredPaymentAmount(6, 50m)), CancellationToken.None);
        Assert.NotEqual(10, result.ClosureId);

        var snapshot = await service.GetClosureAsync(10, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(1234.56m, snapshot!.TotalExpectedBsS);
        Assert.Equal(1200.00m, snapshot.TotalActualBsS);
        Assert.Equal(-34.56m, snapshot.TotalDifferenceBsS);
        var snapshotDetail = Assert.Single(snapshot.Details);
        Assert.Equal(1234.56m, snapshotDetail.ExpectedAmountBsS);
        Assert.Equal(1200.00m, snapshotDetail.ActualAmountBsS);
        Assert.Equal(-34.56m, snapshotDetail.DifferenceBsS);

        var readBack = await context.DailyClosures
            .AsNoTracking()
            .Include(dc => dc.Details)
            .SingleAsync(dc => dc.Id == 10);
        Assert.Equal(1234.56m, readBack.TotalExpectedBsS);
        Assert.Equal(1200.00m, readBack.TotalActualBsS);
        Assert.Equal(-34.56m, readBack.TotalDifferenceBsS);
        Assert.Equal(-34.56m, readBack.Details.Single().DifferenceBsS);
    }

    [Fact]
    public void SalesModule_HasExactlyOneShiftReportDetailResultConstructionSite()
    {
        string repositoryRoot = FindRepositoryRoot();
        string salesModuleDirectory = Path.Combine(repositoryRoot, "Sales.Module");
        char separator = Path.DirectorySeparatorChar;

        int constructionSites = Directory
            .GetFiles(salesModuleDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}bin{separator}") && !path.Contains($"{separator}obj{separator}"))
            .Sum(path => Regex.Matches(File.ReadAllText(path), @"new\s+ShiftReportDetailResult\s*\(").Count);

        Assert.Equal(1, constructionSites);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CommandCenter.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Repository root with CommandCenter.slnx was not found.");
        }

        return directory.FullName;
    }
}
