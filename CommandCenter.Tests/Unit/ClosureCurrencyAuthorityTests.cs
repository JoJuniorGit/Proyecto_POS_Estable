using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Sales.Module;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3b2 (S8/S10): la moneda PERSISTIDA del método de pago es la autoridad del cierre, los
/// recibos y el PDF. El nombre del método solo se usa como fallback legacy cuando no existe una
/// moneda persistida o la navegación no fue cargada. Los montos y las fórmulas no cambian.
/// </summary>
public class ClosureCurrencyAuthorityTests
{
    private const decimal ExchangeRate = 3600m;

    private static SalesDbContext CreateContext() => TestDatabaseFactory.CreateSalesDbContext();

    private static PaymentMethod CreatePersistedMethod(string name, string currency, int id = 1)
        => new()
        {
            Id = id,
            Name = name,
            Currency = currency,
            IsCash = true,
            IsActive = true,
            IsDeleted = false,
            DisplayOrder = id
        };

    private static DailyClosure CreateClosureWithPersistedMethod(
        string name,
        string currency,
        decimal expectedBsS,
        decimal actualBsS,
        decimal differenceBsS)
    {
        var method = CreatePersistedMethod(name, currency);
        return new DailyClosure
        {
            Id = 42,
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin",
            ExchangeRate = ExchangeRate,
            TotalExpectedBsS = expectedBsS,
            TotalActualBsS = actualBsS,
            TotalDifferenceBsS = differenceBsS,
            Details = new List<ClosureDetail>
            {
                new()
                {
                    Id = 421,
                    DailyClosureId = 42,
                    PaymentMethodId = method.Id,
                    PaymentMethodName = name,
                    PaymentMethod = method,
                    ExpectedAmountBsS = expectedBsS,
                    ActualAmountBsS = actualBsS,
                    DifferenceBsS = differenceBsS
                }
            }
        };
    }

    private static async Task SeedSalePaymentAsync(
        SalesDbContext context,
        int saleId,
        decimal amountBsS,
        DateTime now)
    {
        context.Sales.Add(new Sale { Id = saleId, Status = SaleStatus.Completed, Date = now });
        context.SalePayments.Add(new SalePayment
        {
            SaleId = saleId,
            PaymentMethodId = 1,
            AmountBsS = amountBsS,
            CreatedAt = now
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public void MapClosureAndMapDetails_ClassifyPersistedBsS_WhenNameLooksLikeUsd()
    {
        var closure = CreateClosureWithPersistedMethod(
            "Divisas (USD)",
            PaymentMethodCurrencyResolver.LocalCurrency,
            expectedBsS: 10800m,
            actualBsS: 10800m,
            differenceBsS: 0m);

        var dto = ShiftReportMapper.MapClosure(closure);

        var detail = Assert.Single(dto.Details);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, detail.PaymentMethodCurrency);

        string json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
        Assert.Contains("\"paymentMethodCurrency\":\"Bs.S\"", json, StringComparison.Ordinal);

        var reportDetail = Assert.Single(ShiftReportMapper.MapDetails(dto.Details, ExchangeRate));
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, reportDetail.Currency);
        Assert.Equal(10800m, reportDetail.SystemAmount);
        Assert.Equal(10800m, reportDetail.DeclaredAmount);
        Assert.Equal(0m, reportDetail.Difference);
        Assert.Equal(ClosureStatus.Balanced, reportDetail.Status);
    }

    [Fact]
    public void MapClosureAndMapDetails_ClassifyPersistedUsd_WhenNameDoesNotMentionUsd()
    {
        var closure = CreateClosureWithPersistedMethod(
            "Caja Local",
            PaymentMethodCurrencyResolver.Usd,
            expectedBsS: 10800m,
            actualBsS: 10800m,
            differenceBsS: 0m);

        var dto = ShiftReportMapper.MapClosure(closure);

        var detail = Assert.Single(dto.Details);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, detail.PaymentMethodCurrency);

        var reportDetail = Assert.Single(ShiftReportMapper.MapDetails(dto.Details, ExchangeRate));
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, reportDetail.Currency);
        Assert.Equal(3.00m, reportDetail.SystemAmount);
        Assert.Equal(3.00m, reportDetail.DeclaredAmount);
        Assert.Equal(0m, reportDetail.Difference);
    }

    [Fact]
    public void MapDetail_WhenPersistedCurrencyNull_UsesClosureSnapshotName_NotCurrentRenamedMethod()
    {
        var closure = new DailyClosure
        {
            Id = 44,
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin",
            ExchangeRate = ExchangeRate,
            TotalExpectedBsS = 10800m,
            TotalActualBsS = 10800m,
            TotalDifferenceBsS = 0m,
            Details = new List<ClosureDetail>
            {
                new()
                {
                    Id = 441,
                    DailyClosureId = 44,
                    PaymentMethodId = 1,
                    // Snapshot del cierre: no contiene "USD".
                    PaymentMethodName = "Dolares",
                    // El método actual fue renombrado y su Currency es null (fila legacy).
                    PaymentMethod = new PaymentMethod
                    {
                        Id = 1,
                        Name = "Efectivo USD",
                        Currency = null,
                        IsCash = true,
                        IsActive = true
                    },
                    ExpectedAmountBsS = 10800m,
                    ActualAmountBsS = 10800m,
                    DifferenceBsS = 0m
                }
            }
        };

        var dto = ShiftReportMapper.MapClosure(closure);

        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, dto.Details[0].PaymentMethodCurrency);
    }

    [Fact]
    public void GenerateReceiptContent_UsesPersistedBsS_WhenNameLooksLikeUsd()
    {
        var closure = CreateClosureWithPersistedMethod(
            "Divisas (USD)",
            PaymentMethodCurrencyResolver.LocalCurrency,
            expectedBsS: 10800m,
            actualBsS: 10800m,
            differenceBsS: 0m);

        string receipt = DailyClosureService.GenerateReceiptContent(
            ShiftReportMapper.MapClosure(closure),
            isBlind: false);

        string line = Assert.Single(
            receipt.Split('\n'),
            l => l.Contains("Divisas (USD)", StringComparison.Ordinal));
        string withoutName = line.Replace("Divisas (USD)", string.Empty, StringComparison.Ordinal);

        Assert.Contains(PaymentMethodCurrencyResolver.LocalCurrency, withoutName);
        Assert.DoesNotContain(PaymentMethodCurrencyResolver.Usd, withoutName);
    }

    [Fact]
    public void GeneratePdf_UsesPersistedBsS_InCurrencyColumn()
    {
        var closure = CreateClosureWithPersistedMethod(
            "Divisas (USD)",
            PaymentMethodCurrencyResolver.LocalCurrency,
            expectedBsS: 10800m,
            actualBsS: 10800m,
            differenceBsS: 0m);

        byte[] pdf = ClosurePdfGenerator.GeneratePdf(
            ShiftReportMapper.MapClosure(closure),
            isBlind: false);

        Assert.NotEmpty(pdf);
        string ascii = Encoding.ASCII.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", ascii, StringComparison.Ordinal);
        Assert.Contains("(Bs.S) Tj", ascii, StringComparison.Ordinal);
        Assert.DoesNotContain("(USD) Tj", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyDetailWithoutLoadedPaymentMethod_FallsBackToNameRule()
    {
        var closure = new DailyClosure
        {
            Id = 43,
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin",
            ExchangeRate = ExchangeRate,
            TotalExpectedBsS = 10800m,
            TotalActualBsS = 10800m,
            TotalDifferenceBsS = 0m,
            Details = new List<ClosureDetail>
            {
                new()
                {
                    Id = 431,
                    DailyClosureId = 43,
                    PaymentMethodId = 1,
                    PaymentMethodName = "Efectivo USD",
                    ExpectedAmountBsS = 10800m,
                    ActualAmountBsS = 10800m,
                    DifferenceBsS = 0m
                }
            }
        };

        var dto = ShiftReportMapper.MapClosure(closure);

        // Sin navegación cargada no hay moneda persistida que publicar; el consumidor aplica la
        // regla histórica por nombre. Esto conserva además el contrato JSON legado exacto.
        Assert.Null(dto.Details[0].PaymentMethodCurrency);

        var reportDetail = Assert.Single(ShiftReportMapper.MapDetails(dto.Details, ExchangeRate));
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, reportDetail.Currency);
        Assert.Equal(3.00m, reportDetail.SystemAmount);
        Assert.Equal(3.00m, reportDetail.DeclaredAmount);

        string receipt = DailyClosureService.GenerateReceiptContent(dto, isBlind: false);
        string line = Assert.Single(
            receipt.Split('\n'),
            l => l.Contains("Efectivo USD", StringComparison.Ordinal));
        string withoutName = line.Replace("Efectivo USD", string.Empty, StringComparison.Ordinal);
        Assert.Contains(PaymentMethodCurrencyResolver.Usd, withoutName);

        byte[] pdf = ClosurePdfGenerator.GeneratePdf(dto, isBlind: false);
        Assert.Contains("(USD) Tj", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetClosureAsync_LoadsPaymentMethodNavigation_AndReturnsPersistedCurrency()
    {
        using var context = CreateContext();
        var method = CreatePersistedMethod("Divisas (USD)", PaymentMethodCurrencyResolver.LocalCurrency);
        context.PaymentMethods.Add(method);
        context.DailyClosures.Add(new DailyClosure
        {
            Id = 7,
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin",
            ExchangeRate = ExchangeRate,
            TotalExpectedBsS = 10800m,
            TotalActualBsS = 10800m,
            TotalDifferenceBsS = 0m,
            Details = new List<ClosureDetail>
            {
                new()
                {
                    Id = 71,
                    DailyClosureId = 7,
                    PaymentMethodId = method.Id,
                    PaymentMethodName = method.Name,
                    ExpectedAmountBsS = 10800m,
                    ActualAmountBsS = 10800m,
                    DifferenceBsS = 0m
                }
            }
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = DailyClosureTestHelper.CreateService(context);
        var dto = await service.GetClosureAsync(7);

        Assert.NotNull(dto);
        var detail = Assert.Single(dto!.Details);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, detail.PaymentMethodCurrency);

        var reportDetail = Assert.Single(ShiftReportMapper.MapDetails(dto.Details, dto.ExchangeRate));
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, reportDetail.Currency);
        Assert.Equal(10800m, reportDetail.SystemAmount);
    }

    [Fact]
    public async Task GetLatestClosureAsync_LoadsPaymentMethodNavigation_AndReturnsPersistedCurrency()
    {
        using var context = CreateContext();
        var method = CreatePersistedMethod("Divisas (USD)", PaymentMethodCurrencyResolver.LocalCurrency);
        context.PaymentMethods.Add(method);
        context.DailyClosures.Add(new DailyClosure
        {
            Id = 8,
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin",
            ExchangeRate = ExchangeRate,
            TotalExpectedBsS = 10800m,
            TotalActualBsS = 10800m,
            TotalDifferenceBsS = 0m,
            Details = new List<ClosureDetail>
            {
                new()
                {
                    Id = 81,
                    DailyClosureId = 8,
                    PaymentMethodId = method.Id,
                    PaymentMethodName = method.Name,
                    ExpectedAmountBsS = 10800m,
                    ActualAmountBsS = 10800m,
                    DifferenceBsS = 0m
                }
            }
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = DailyClosureTestHelper.CreateService(context);
        var dto = await service.GetLatestClosureAsync();

        Assert.NotNull(dto);
        var detail = Assert.Single(dto!.Details);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, detail.PaymentMethodCurrency);
    }

    [Fact]
    public async Task GetExpectedTotalsByPaymentMethodAsync_ReturnsPersistedCurrency()
    {
        using var context = CreateContext();
        context.PaymentMethods.AddRange(
            CreatePersistedMethod("Divisas (USD)", PaymentMethodCurrencyResolver.LocalCurrency, id: 1),
            CreatePersistedMethod("Caja Local", PaymentMethodCurrencyResolver.Usd, id: 2));
        await context.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(context);
        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);

        Assert.Equal(
            PaymentMethodCurrencyResolver.LocalCurrency,
            totals.Single(t => t.PaymentMethodId == 1).PaymentMethodCurrency);
        Assert.Equal(
            PaymentMethodCurrencyResolver.Usd,
            totals.Single(t => t.PaymentMethodId == 2).PaymentMethodCurrency);
    }

    [Fact]
    public async Task CreateClosure_UsesPersistedBsS_ForDeclaredAmountsAndTotals()
    {
        using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.PaymentMethods.Add(CreatePersistedMethod("Divisas (USD)", PaymentMethodCurrencyResolver.LocalCurrency));
        await SeedSalePaymentAsync(context, saleId: 501, amountBsS: 1000m, now);

        var service = DailyClosureTestHelper.CreateService(context);
        var result = await service.CreateClosureFromCommandAsync(
            new CreateClosureCommand(
                now,
                "Admin",
                "V-00000000",
                new List<DeclaredPaymentAmount> { new(1, 1000m) }),
            CancellationToken.None);

        var detail = Assert.Single(result.Details);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, detail.Currency);
        Assert.Equal(1000m, detail.DeclaredAmount);
        Assert.Equal(1000m, detail.SystemAmount);
        Assert.Equal(0m, detail.Difference);
        Assert.Equal(ClosureStatus.Balanced, detail.Status);

        var saved = await context.DailyClosures.AsNoTracking()
            .SingleAsync(closure => closure.Id == result.ClosureId);
        Assert.Equal(1000m, saved.TotalExpectedBsS);
        Assert.Equal(1000m, saved.TotalActualBsS);
        Assert.Equal(0m, saved.TotalDifferenceBsS);
    }

    [Fact]
    public async Task CreateClosure_UsesPersistedUsd_ForDeclaredAmountConversion()
    {
        using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.PaymentMethods.Add(CreatePersistedMethod("Caja Local", PaymentMethodCurrencyResolver.Usd));
        await SeedSalePaymentAsync(context, saleId: 502, amountBsS: 5000m, now);

        var service = DailyClosureTestHelper.CreateService(context);
        var result = await service.CreateClosureFromCommandAsync(
            new CreateClosureCommand(
                now,
                "Admin",
                "V-00000000",
                new List<DeclaredPaymentAmount> { new(1, 100m) }),
            CancellationToken.None);

        var detail = Assert.Single(result.Details);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, detail.Currency);
        Assert.Equal(100m, detail.DeclaredAmount);
        Assert.Equal(100m, detail.SystemAmount);
        Assert.Equal(0m, detail.Difference);
        Assert.Equal(ClosureStatus.Balanced, detail.Status);

        var saved = await context.DailyClosures.AsNoTracking()
            .SingleAsync(closure => closure.Id == result.ClosureId);
        Assert.Equal(5000m, saved.TotalExpectedBsS);
        Assert.Equal(5000m, saved.TotalActualBsS);
        Assert.Equal(0m, saved.TotalDifferenceBsS);
    }

    [Fact]
    public async Task CreateClosure_MissingCashMethod_UsesPersistedCurrencyFromMethodEntity()
    {
        using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.PaymentMethods.Add(CreatePersistedMethod("Divisas (USD)", PaymentMethodCurrencyResolver.LocalCurrency));
        await SeedSalePaymentAsync(context, saleId: 503, amountBsS: 1000m, now);

        var service = DailyClosureTestHelper.CreateService(context);
        var result = await service.CreateClosureFromCommandAsync(
            new CreateClosureCommand(
                now,
                "Admin",
                "V-00000000",
                new List<DeclaredPaymentAmount>()),
            CancellationToken.None);

        var detail = Assert.Single(result.Details);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, detail.Currency);
        Assert.Equal(0m, detail.DeclaredAmount);
        Assert.Equal(1000m, detail.SystemAmount);
        Assert.Equal(-1000m, detail.Difference);
        Assert.Equal(ClosureStatus.Shortage, detail.Status);

        var saved = await context.DailyClosures.AsNoTracking()
            .SingleAsync(closure => closure.Id == result.ClosureId);
        Assert.Equal(1000m, saved.TotalExpectedBsS);
        Assert.Equal(0m, saved.TotalActualBsS);
        Assert.Equal(-1000m, saved.TotalDifferenceBsS);
    }
}
