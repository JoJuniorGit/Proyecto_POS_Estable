using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.TestHelpers;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Moq;
using Sales.Module;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Residual 8.187-8.188: arqueo fisico USD del cierre diario. El esperado se congela desde el
/// bucket USD de la sesion (mismo calculo por moneda de la gaveta, sin heuristica de nombres),
/// la diferencia es contado - esperado y el silencio del campo conserva el contrato previo.
/// La respuesta respeta el cierre ciego 8.153: solo Admin/Manager reciben esperado y diferencia.
/// </summary>
public class DailyClosureUsdCountTests
{
    private const decimal ExchangeRate = 50m;
    private const decimal ExpectedPhysicalUsd = 12.5m;

    private static SalesDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private static async Task SeedOpenSessionAndUsdMethodAsync(SalesDbContext context)
    {
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 1,
            Name = "Efectivo USD",
            IsCash = true,
            IsActive = true,
            IsDeleted = false,
            DisplayOrder = 1
        });
        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 1000m,
            OpeningExchangeRate = ExchangeRate,
            OpeningBalanceUsd = 10m
        });
        await context.SaveChangesAsync();
    }

    private static CreateClosureCommand CreateCommand(decimal? actualClosingBalanceUsd)
    {
        return new CreateClosureCommand(
            ClosureDateUtc: DateTime.UtcNow,
            UserId: "1",
            Observation: "V-00000000",
            Declarations: new List<DeclaredPaymentAmount> { new(1, 5m) },
            ActualClosingBalanceUsd: actualClosingBalanceUsd);
    }

    private static (
        DailyClosureService Service,
        Mock<ICashDrawerService> CashDrawer,
        Mock<ITodayExchangeRateProvider> RateProvider) CreateService(SalesDbContext context)
    {
        var rateProvider = new Mock<ITodayExchangeRateProvider>();
        rateProvider.Setup(r => r.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExchangeRate);

        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer.Setup(c => c.GetPhysicalBalanceAsync(
                1,
                PettyCashCurrency.Usd,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExpectedPhysicalUsd);

        return (new DailyClosureService(context, rateProvider.Object, cashDrawer.Object), cashDrawer, rateProvider);
    }

    [Fact]
    public async Task CreateClosure_WithUsdCount_ComputesExpectedFromSessionBucketAndDifference()
    {
        using var context = CreateInMemoryContext();
        await SeedOpenSessionAndUsdMethodAsync(context);
        var (service, cashDrawer, _) = CreateService(context);

        var result = await service.CreateClosureFromCommandAsync(
            CreateCommand(actualClosingBalanceUsd: 13.25m),
            CancellationToken.None);

        Assert.Equal(ExpectedPhysicalUsd, result.ExpectedClosingBalanceUsd);
        Assert.Equal(13.25m, result.ActualClosingBalanceUsd);
        Assert.Equal(0.75m, result.DifferenceUsd);

        cashDrawer.Verify(c => c.GetPhysicalBalanceAsync(
            1,
            PettyCashCurrency.Usd,
            It.IsAny<CancellationToken>()), Times.Once);

        context.ChangeTracker.Clear();
        var persisted = await context.DailyClosures.AsNoTracking().SingleAsync();
        Assert.Equal(ExpectedPhysicalUsd, persisted.ExpectedClosingBalanceUsd);
        Assert.Equal(13.25m, persisted.ActualClosingBalanceUsd);
        Assert.Equal(0.75m, persisted.DifferenceUsd);
    }

    [Fact]
    public async Task CreateClosure_WithRealDrawer_ReusesUsdBucketWithoutMixingLocalCurrency()
    {
        using var context = CreateInMemoryContext();
        context.PaymentMethods.AddRange(
            new PaymentMethod
            {
                Id = 1,
                Name = "Efectivo USD",
                IsCash = true,
                IsActive = true,
                IsDeleted = false,
                DisplayOrder = 1
            },
            new PaymentMethod
            {
                Id = 2,
                Name = "Efectivo Bs.S",
                IsCash = true,
                IsActive = true,
                IsDeleted = false,
                DisplayOrder = 2
            });
        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 1000m,
            OpeningExchangeRate = ExchangeRate,
            OpeningBalanceUsd = 10m
        });
        context.CashTransactions.AddRange(
            new CashTransaction
            {
                SessionId = 1,
                TransactionTime = DateTime.UtcNow,
                Type = CashTransactionType.Income,
                Source = CashTransactionSource.SalePayment,
                AmountLocal = 250m,
                AmountUsd = 5m,
                ExchangeRate = ExchangeRate,
                IsPhysicalCash = true,
                PaymentMethodId = 1,
                Description = "Venta fisica USD"
            },
            new CashTransaction
            {
                SessionId = 1,
                TransactionTime = DateTime.UtcNow,
                Type = CashTransactionType.Expense,
                Source = CashTransactionSource.SalePayment,
                AmountLocal = 50m,
                AmountUsd = 1m,
                ExchangeRate = ExchangeRate,
                IsPhysicalCash = true,
                PaymentMethodId = 1,
                Description = "Vuelto fisico USD"
            },
            new CashTransaction
            {
                SessionId = 1,
                TransactionTime = DateTime.UtcNow,
                Type = CashTransactionType.Income,
                Source = CashTransactionSource.SalePayment,
                AmountLocal = 500m,
                AmountUsd = 0m,
                ExchangeRate = ExchangeRate,
                IsPhysicalCash = true,
                PaymentMethodId = 2,
                Description = "Venta fisica Bs.S"
            });
        await context.SaveChangesAsync();

        var rateProvider = new Mock<ITodayExchangeRateProvider>();
        rateProvider.Setup(r => r.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExchangeRate);
        var service = new DailyClosureService(context, rateProvider.Object, new CashDrawerService(context));

        var result = await service.CreateClosureFromCommandAsync(
            CreateCommand(actualClosingBalanceUsd: 15.5m),
            CancellationToken.None);

        // 10 (apertura USD) + 5 (venta USD) - 1 (vuelto USD) = 14; la venta Bs.S no contamina.
        Assert.Equal(14m, result.ExpectedClosingBalanceUsd);
        Assert.Equal(15.5m, result.ActualClosingBalanceUsd);
        Assert.Equal(1.5m, result.DifferenceUsd);
    }

    [Fact]
    public async Task CreateClosure_WithoutUsdCount_PreservesLegacyNullContract()
    {
        using var context = CreateInMemoryContext();
        await SeedOpenSessionAndUsdMethodAsync(context);
        var (service, cashDrawer, _) = CreateService(context);

        var result = await service.CreateClosureFromCommandAsync(
            CreateCommand(actualClosingBalanceUsd: null),
            CancellationToken.None);

        Assert.Null(result.ExpectedClosingBalanceUsd);
        Assert.Null(result.ActualClosingBalanceUsd);
        Assert.Null(result.DifferenceUsd);
        cashDrawer.Verify(c => c.GetPhysicalBalanceAsync(
            It.IsAny<int>(),
            It.IsAny<PettyCashCurrency>(),
            It.IsAny<CancellationToken>()), Times.Never);

        context.ChangeTracker.Clear();
        var persisted = await context.DailyClosures.AsNoTracking().SingleAsync();
        Assert.Null(persisted.ExpectedClosingBalanceUsd);
        Assert.Null(persisted.ActualClosingBalanceUsd);
        Assert.Null(persisted.DifferenceUsd);
    }

    [Fact]
    public async Task CreateClosure_WithNegativeUsdCount_RejectsBeforePersisting()
    {
        using var context = CreateInMemoryContext();
        await SeedOpenSessionAndUsdMethodAsync(context);
        var (service, _, _) = CreateService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateClosureFromCommandAsync(
                CreateCommand(actualClosingBalanceUsd: -0.01m),
                CancellationToken.None));

        Assert.Contains("USD", ex.Message);
        Assert.Empty(await context.DailyClosures.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateClosure_WithUsdCountButNoActiveSession_PersistsCountWithoutInventingExpected()
    {
        using var context = CreateInMemoryContext();
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 1,
            Name = "Efectivo USD",
            IsCash = true,
            IsActive = true,
            IsDeleted = false,
            DisplayOrder = 1
        });
        await context.SaveChangesAsync();
        var (service, cashDrawer, _) = CreateService(context);

        var result = await service.CreateClosureFromCommandAsync(
            CreateCommand(actualClosingBalanceUsd: 13.25m),
            CancellationToken.None);

        Assert.Null(result.ExpectedClosingBalanceUsd);
        Assert.Equal(13.25m, result.ActualClosingBalanceUsd);
        Assert.Null(result.DifferenceUsd);
        cashDrawer.Verify(c => c.GetPhysicalBalanceAsync(
            It.IsAny<int>(),
            It.IsAny<PettyCashCurrency>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SalesModel_MapsDailyClosureUsdCountWithFourDecimals()
    {
        using var context = CreateNpgsqlModelContext();
        var closure = context.Model.FindEntityType(typeof(DailyClosure));

        Assert.NotNull(closure);
        foreach (string propertyName in new[]
                 {
                     nameof(DailyClosure.ExpectedClosingBalanceUsd),
                     nameof(DailyClosure.ActualClosingBalanceUsd),
                     nameof(DailyClosure.DifferenceUsd)
                 })
        {
            var property = closure!.FindProperty(propertyName);
            Assert.NotNull(property);
            Assert.Equal("numeric(18,4)", property!.GetColumnType());
            Assert.True(property.IsNullable);
        }
    }

    [Fact]
    public void DailyClosureResponse_OmitsUsdFieldsWhenNull_AndSerializesThemOtherwise()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var legacy = new DailyClosureResponseDto(
            7,
            new DateTime(2026, 9, 17, 12, 30, 0, DateTimeKind.Utc),
            "Admin",
            50m,
            0m,
            0m,
            0m,
            null,
            Array.Empty<ClosureDetailResponseDto>());

        string legacyJson = JsonSerializer.Serialize(legacy, options);
        Assert.DoesNotContain("expectedClosingBalanceUsd", legacyJson);
        Assert.DoesNotContain("actualClosingBalanceUsd", legacyJson);
        Assert.DoesNotContain("differenceUsd", legacyJson);

        var withUsd = legacy with
        {
            ExpectedClosingBalanceUsd = 12.5m,
            ActualClosingBalanceUsd = 13.25m,
            DifferenceUsd = 0.75m
        };
        string usdJson = JsonSerializer.Serialize(withUsd, options);
        Assert.Contains("\"expectedClosingBalanceUsd\":12.5", usdJson);
        Assert.Contains("\"actualClosingBalanceUsd\":13.25", usdJson);
        Assert.Contains("\"differenceUsd\":0.75", usdJson);
    }

    [Fact]
    public async Task DailyClosureController_CashierResponse_BlindsExpectedAndDifferenceUsd()
    {
        var (controller, _) = CreateDailyClosureController("Cashier", "2");

        var result = await controller.CreateClosureAsync(
            new CreateClosureRequest
            {
                Details = new List<CreateClosureDetailRequest>
                {
                    new() { PaymentMethodId = 1, ActualAmountBsS = 500m }
                },
                ActualClosingBalanceUsd = 13.25m
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<CloseShiftResult>(ok.Value);
        Assert.Null(body.ExpectedClosingBalanceUsd);
        Assert.Null(body.DifferenceUsd);
        Assert.Equal(13.25m, body.ActualClosingBalanceUsd);
    }

    [Fact]
    public async Task DailyClosureController_AdminResponse_KeepsUsdReconciliation()
    {
        var (controller, _) = CreateDailyClosureController("Admin", "1");

        var result = await controller.CreateClosureAsync(
            new CreateClosureRequest
            {
                Details = new List<CreateClosureDetailRequest>
                {
                    new() { PaymentMethodId = 1, ActualAmountBsS = 500m }
                },
                ActualClosingBalanceUsd = 13.25m
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<CloseShiftResult>(ok.Value);
        Assert.Equal(ExpectedPhysicalUsd, body.ExpectedClosingBalanceUsd);
        Assert.Equal(13.25m, body.ActualClosingBalanceUsd);
        Assert.Equal(0.75m, body.DifferenceUsd);
    }

    [Fact]
    public async Task ShiftsClose_CashierResponse_BlindsExpectedAndDifferenceUsd()
    {
        var service = new Mock<IDailyClosureService>();
        service.Setup(s => s.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateUsdResult());

        var controller = CreateShiftsController(service, "Cashier", "2");
        var result = await controller.CloseShiftAsync(
            new CloseShiftRequest
            {
                DeclaredAmounts = new List<DeclaredAmountDto> { new() { PaymentMethodId = 1, Amount = 13.25m } },
                ActualClosingBalanceUsd = 13.25m
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ShiftReportDto>(ok.Value);
        Assert.Null(body.ExpectedClosingBalanceUsd);
        Assert.Null(body.DifferenceUsd);
        Assert.Equal(13.25m, body.ActualClosingBalanceUsd);
    }

    [Fact]
    public async Task ShiftsClose_AdminResponse_KeepsUsdReconciliation()
    {
        var service = new Mock<IDailyClosureService>();
        service.Setup(s => s.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateUsdResult());

        var controller = CreateShiftsController(service, "Admin", "1");
        var result = await controller.CloseShiftAsync(
            new CloseShiftRequest
            {
                DeclaredAmounts = new List<DeclaredAmountDto> { new() { PaymentMethodId = 1, Amount = 13.25m } },
                ActualClosingBalanceUsd = 13.25m
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ShiftReportDto>(ok.Value);
        Assert.Equal(ExpectedPhysicalUsd, body.ExpectedClosingBalanceUsd);
        Assert.Equal(13.25m, body.ActualClosingBalanceUsd);
        Assert.Equal(0.75m, body.DifferenceUsd);
    }

    [Fact]
    public async Task ShiftReport_CashierOwner_BlindsUsdReconciliation()
    {
        var service = new Mock<IDailyClosureService>();
        service.Setup(s => s.GetClosureAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateUsdClosureDto(userId: "2"));
        service.Setup(s => s.GetCashierDisplayNameAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Cajero");

        var controller = CreateShiftsController(service, "Cashier", "2");
        var result = await controller.GetReportByIdAsync(9, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ShiftReportDto>(ok.Value);
        Assert.Null(body.ExpectedClosingBalanceUsd);
        Assert.Null(body.DifferenceUsd);
        Assert.Equal(13.25m, body.ActualClosingBalanceUsd);
    }

    [Fact]
    public void Migration_AddDailyClosureUsdCountRev8188_IsAdditiveGuardedAndMatchesSnapshot()
    {
        using var context = CreateNpgsqlModelContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        string migrationKey = Assert.Single(
            migrations.Migrations.Keys,
            key => key.Contains("AddDailyClosureUsdCountRev8188"));

        var migration = (Migration)Activator.CreateInstance(migrations.Migrations[migrationKey].AsType())!;
        Assert.Empty(migration.UpOperations.OfType<DropTableOperation>());
        Assert.Empty(migration.UpOperations.OfType<DropColumnOperation>());

        string script = context.GetService<IMigrator>().GenerateScript(
            fromMigration: "20261018231728_AddDailyClosurePettyCashSessionRev8188",
            toMigration: migrationKey);

        Assert.Contains("information_schema.columns", script);
        Assert.Contains("\"DailyClosures\" ADD COLUMN IF NOT EXISTS \"ExpectedClosingBalanceUsd\"", script);
        Assert.Contains("\"DailyClosures\" ADD COLUMN IF NOT EXISTS \"ActualClosingBalanceUsd\"", script);
        Assert.Contains("\"DailyClosures\" ADD COLUMN IF NOT EXISTS \"DifferenceUsd\"", script);
        Assert.Contains("numeric(18,4) NULL", script);

        string downSql = string.Join(
            Environment.NewLine,
            migration.DownOperations.OfType<SqlOperation>().Select(operation => operation.Sql));
        Assert.Contains("DROP COLUMN IF EXISTS \"ExpectedClosingBalanceUsd\"", downSql);
        Assert.Contains("DROP COLUMN IF EXISTS \"ActualClosingBalanceUsd\"", downSql);
        Assert.Contains("DROP COLUMN IF EXISTS \"DifferenceUsd\"", downSql);

        var snapshot = migrations.ModelSnapshot;
        Assert.NotNull(snapshot);
        var initializedSnapshot = context.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshot!.Model, designTime: true);
        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(
                initializedSnapshot.GetRelationalModel(),
                context.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.True(
            differences.Count == 0,
            $"El ModelSnapshot de Sales.Module tiene drift contra el modelo EF: {differences.Count} operaciones pendientes.");
    }

    private static CloseShiftResult CreateUsdResult() => new(
        42,
        "Admin",
        "V-12345678",
        DateTime.UtcNow,
        ExchangeRate,
        new List<ShiftReportDetailResult>
        {
            new(1, "Efectivo USD", PaymentMethodCurrencyResolver.Usd, 13.25m, ExpectedPhysicalUsd, 0.75m, ClosureStatus.Surplus)
        },
        ExpectedPhysicalUsd,
        13.25m,
        0.75m);

    private static DailyClosureResponseDto CreateUsdClosureDto(string userId) => new(
        9,
        DateTime.UtcNow,
        userId,
        ExchangeRate,
        500m,
        500m,
        0m,
        "V-00000000",
        new List<ClosureDetailResponseDto>
        {
            new(1, 9, 1, "Efectivo USD", 500m, 662.5m, 162.5m)
        },
        ExpectedClosingBalanceUsd: ExpectedPhysicalUsd,
        ActualClosingBalanceUsd: 13.25m,
        DifferenceUsd: 0.75m);

    private static (DailyClosureController Controller, Mock<IDailyClosureService> Service) CreateDailyClosureController(
        string role,
        string userId)
    {
        var service = new Mock<IDailyClosureService>();
        service.Setup(s => s.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateUsdResult());

        var controller = new DailyClosureController(service.Object, new Mock<ICurrentUserService>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, userId),
                        new Claim(ClaimTypes.Role, role)
                    }, "TestAuth"))
                }
            }
        };
        controller.ControllerContext.HttpContext.Request.Headers["Idempotency-Key"] =
            "CLOSURE-USD-" + Guid.NewGuid().ToString("N");

        return (controller, service);
    }

    private static ShiftsController CreateShiftsController(
        Mock<IDailyClosureService> service,
        string role,
        string userId)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserId).Returns(userId);

        return new ShiftsController(service.Object, currentUser.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, userId),
                        new Claim(ClaimTypes.SerialNumber, "V-00000000"),
                        new Claim(ClaimTypes.Role, role)
                    }, "TestAuth"))
                }
            }
        };
    }

    private static SalesDbContext CreateNpgsqlModelContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused")
            .Options;
        return new SalesDbContext(options);
    }
}
