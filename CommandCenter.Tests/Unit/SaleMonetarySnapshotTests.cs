using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3a (S6): snapshot aditivo del perfil monetario vigente al completar una venta.
/// La captura no puede alterar montos, tasas ni redondeos: los totales históricos siguen
/// siendo la fuente de cálculo y estas pruebas los fijan contra los valores canónicos.
/// </summary>
public class SaleMonetarySnapshotTests
{
    private static SalesService CreateService(
        SalesDbContext context,
        IMonetaryProfileService? monetaryProfileService = null,
        ILogger<SalesService>? logger = null)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetCashAdvanceProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var settings = new Mock<ISystemSettingsService>();

        // The optional monetary profile service is the last constructor parameter; named
        // arguments keep every historical positional caller untouched.
        return new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
            settings.Object,
            monetaryProfileService: monetaryProfileService,
            logger: logger);
    }

    private static async Task<Sale> SeedPendingSaleAsync(SalesDbContext context, decimal totalUsd = 100m, decimal rate = 50m)
    {
        var sale = new Sale
        {
            TotalUSD = totalUsd,
            Subtotal = totalUsd,
            AppliedRate = rate,
            TotalBsS = totalUsd * rate,
            SubtotalBsS = totalUsd * rate,
            Status = SaleStatus.Pending
        };

        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 1,
            Name = "Punto de Venta",
            IsCash = false,
            IsActive = true,
            Currency = "Bs.S"
        });

        await context.SaveChangesAsync();
        return sale;
    }

    private static Mock<IMonetaryProfileService> CreateProfileMock(MonetaryProfileDto profile)
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        return mock;
    }

    private static List<PaymentInfo> ExactPayment(decimal amountUsd, decimal amountLocal) =>
        new() { new PaymentInfo(1, amountUsd, amountLocal, null) };

    [Fact]
    public async Task CompletedSale_WithoutMonetaryProfileService_PersistsLegacySnapshotAndKeepsFinancialTotals()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context);
        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(sale.Id, 50m, ExactPayment(100m, 5000m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal("USD", saved.PrimaryCurrencyCode);
        Assert.Equal("VES", saved.SecondaryCurrencyCode);
        Assert.Equal("Dual", saved.MonetaryMode);

        // Financial snapshot unchanged: the additive profile snapshot never recalculates amounts.
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(5000m, saved.TotalBsS);
        Assert.Equal(50m, saved.AppliedRate);
    }

    [Fact]
    public async Task CompletedSale_WithDualProfile_PersistsSnapshotAndExposesItThroughSaleDto()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var profile = CreateProfileMock(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "EUR",
            PrimaryCurrencySymbol = "€",
            SecondaryCurrencyCode = "GBP",
            SecondaryCurrencySymbol = "£",
            IsConfigured = true
        });

        var service = CreateService(context, profile.Object);
        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(sale.Id, 50m, ExactPayment(100m, 5000m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal("EUR", saved.PrimaryCurrencyCode);
        Assert.Equal("GBP", saved.SecondaryCurrencyCode);
        Assert.Equal("Dual", saved.MonetaryMode);
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(5000m, saved.TotalBsS);
        Assert.Equal(50m, saved.AppliedRate);

        var dto = await service.GetSaleAsync(sale.Id);
        Assert.Equal("EUR", dto.PrimaryCurrencyCode);
        Assert.Equal("GBP", dto.SecondaryCurrencyCode);
        Assert.Equal("Dual", dto.MonetaryMode);
    }

    [Fact]
    public async Task CompletedSale_WithSingleProfile_PersistsNullSecondaryCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var profile = CreateProfileMock(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Single,
            PrimaryCurrencyCode = "COP",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = null,
            SecondaryCurrencySymbol = null,
            IsConfigured = true
        });

        var service = CreateService(context, profile.Object);
        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(sale.Id, 50m, ExactPayment(100m, 5000m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal("COP", saved.PrimaryCurrencyCode);
        Assert.Null(saved.SecondaryCurrencyCode);
        Assert.Equal("Single", saved.MonetaryMode);
    }

    [Fact]
    public async Task CompletedSale_WithSingleProfile_OnRelationalProvider_UpdatesSecondaryToNull()
    {
        // The real Single path is an UPDATE of the seeded/default VES value to NULL. SQLite exercises
        // the relational update semantics that InMemory does not enforce.
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var profile = CreateProfileMock(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "COP",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null,
                IsConfigured = true
            });

            var service = CreateService(context, profile.Object);
            var sale = new Sale
            {
                TotalUSD = 100m,
                Subtotal = 100m,
                AppliedRate = 50m,
                TotalBsS = 5000m,
                SubtotalBsS = 5000m,
                Status = SaleStatus.Pending
            };
            context.Sales.Add(sale);
            // Id 100 avoids the HasData "Cash" method (Id 1), which would require a real drawer row.
            context.PaymentMethods.Add(new PaymentMethod
            {
                Id = 100,
                Name = "Punto de Venta Test",
                IsCash = false,
                IsActive = true,
                Currency = "Bs.S"
            });
            await context.SaveChangesAsync();

            await service.CompleteSaleAsync(
                sale.Id,
                50m,
                new List<PaymentInfo> { new(100, 100m, 5000m, null) });

            var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
            Assert.Equal(SaleStatus.Completed, saved.Status);
            Assert.Equal("COP", saved.PrimaryCurrencyCode);
            Assert.Null(saved.SecondaryCurrencyCode);
            Assert.Equal("Single", saved.MonetaryMode);
        }
    }

    [Fact]
    public async Task HoldSale_WithFullInitialPayment_WithDualProfile_PersistsSnapshotAndKeepsHistoricalAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.Add(new Customer
        {
            Id = 2,
            Name = "Cliente Real",
            CedulaOrRif = "V-12345678",
            IsDefault = false,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var profile = CreateProfileMock(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "EUR",
            PrimaryCurrencySymbol = "€",
            SecondaryCurrencyCode = "GBP",
            SecondaryCurrencySymbol = "£",
            IsConfigured = true
        });

        var service = CreateService(context, profile.Object);
        var sale = await SeedPendingSaleAsync(context);

        var dto = await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
        {
            CustomerId = 2,
            ExchangeRate = 50m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 1,
                AmountUSD = 100m,
                AmountBsS = 5000m,
                ExchangeRate = 50m
            }
        });

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal("EUR", saved.PrimaryCurrencyCode);
        Assert.Equal("GBP", saved.SecondaryCurrencyCode);
        Assert.Equal("Dual", saved.MonetaryMode);
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(5000m, saved.TotalBsS);
        Assert.Equal(50m, saved.AppliedRate);

        Assert.Equal("EUR", dto.PrimaryCurrencyCode);
        Assert.Equal("GBP", dto.SecondaryCurrencyCode);
        Assert.Equal("Dual", dto.MonetaryMode);
    }

    [Fact]
    public async Task CashAdvance_WithDualProfile_PersistsSnapshotAndKeepsHistoricalAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.Add(new Customer
        {
            Id = 1,
            Name = "Consumidor Final",
            CedulaOrRif = "V-00000000",
            IsDefault = true,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var profile = CreateProfileMock(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "EUR",
            PrimaryCurrencySymbol = "€",
            SecondaryCurrencyCode = "GBP",
            SecondaryCurrencySymbol = "£",
            IsConfigured = true
        });

        var service = CreateService(context, profile.Object);

        var dto = await service.CreateCashAdvanceSaleAsync(
            requestedAmountLocal: 1000m,
            commissionAmountLocal: 70m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia",
            isTransfer: true,
            exchangeRate: 50m,
            cashierId: 7);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == dto.Id);
        Assert.Equal("EUR", saved.PrimaryCurrencyCode);
        Assert.Equal("GBP", saved.SecondaryCurrencyCode);
        Assert.Equal("Dual", saved.MonetaryMode);

        // Cash advances create the completed sale directly: same canonical historical amounts.
        Assert.Equal(21.4m, saved.TotalUSD);
        Assert.Equal(1070m, saved.TotalBsS);
        Assert.Equal(50m, saved.AppliedRate);
    }

    [Fact]
    public async Task CompletedSale_WhenProfileReadFails_FallsBackToLegacySnapshotWithoutFailingTheSale()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("settings store unavailable"));
        var logger = new Mock<ILogger<SalesService>>();

        var service = CreateService(context, profile.Object, logger.Object);
        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(sale.Id, 50m, ExactPayment(100m, 5000m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal("USD", saved.PrimaryCurrencyCode);
        Assert.Equal("VES", saved.SecondaryCurrencyCode);
        Assert.Equal("Dual", saved.MonetaryMode);
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(5000m, saved.TotalBsS);

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("perfil monetario", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SaleWithExplicitNullSecondaryCurrency_PersistsNullDespiteColumnDefaultOnRelationalProvider()
    {
        // S6 decision: Single mode persists NULL while legacy rows are backfilled to VES by the
        // column default. SQLite enforces column defaults, so this guards the runtime insert
        // semantics that InMemory does not exercise.
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            context.Sales.Add(new Sale
            {
                Status = SaleStatus.Completed,
                TotalUSD = 1m,
                AppliedRate = 1m,
                TotalBsS = 1m,
                PrimaryCurrencyCode = "COP",
                SecondaryCurrencyCode = null,
                MonetaryMode = "Single"
            });

            await context.SaveChangesAsync();

            var saved = await context.Sales.AsNoTracking().SingleAsync();
            Assert.Equal("COP", saved.PrimaryCurrencyCode);
            Assert.Null(saved.SecondaryCurrencyCode);
            Assert.Equal("Single", saved.MonetaryMode);
        }
    }
}
