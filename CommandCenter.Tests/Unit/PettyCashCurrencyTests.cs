using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3c4 (S7/F3c4): petty cash resolves the local drawer and its rate from the active
/// monetary profile. <c>PettyCashCurrency.BsS</c> means local cash (COP in Dual USD/COP, Bs.S in
/// Dual USD/VES/Single VES) and <c>Usd</c> is only valid when USD belongs to the profile. The
/// manual pair keeps 8dp for valuation and the 18,4 snapshot at 4dp; reversal mirrors the expense.
/// </summary>
public class PettyCashCurrencyTests
{
    private const decimal BcvRate = 36.55m;
    private const decimal ManualCopRate = 4000.12345678m;

    private sealed class PettyCashStack : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SalesDbContext Context { get; }
        public CashDrawerService Drawer { get; }
        public PettyCashService Service { get; }
        public Mock<IInventoryService> Inventory { get; } = new();
        public Mock<ICurrencyRateService> CurrencyRates { get; } = new();

        public PettyCashStack(IMonetaryProfileService? profile = null, decimal bcvRate = BcvRate)
        {
            (Context, _connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
            Drawer = new CashDrawerService(Context, profile);
            Inventory
                .Setup(inventory => inventory.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(bcvRate);
            CurrencyRates
                .Setup(rates => rates.GetRateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("No hay tasa manual configurada para el par solicitado."));
            Service = new PettyCashService(
                Context,
                Drawer,
                Inventory.Object,
                new Mock<ISystemSettingsService>().Object,
                logger: null,
                monetaryProfileService: profile,
                currencyRateService: CurrencyRates.Object);
        }

        public void ConfigureManualRate(string secondaryCode, decimal rate)
        {
            CurrencyRates
                .Setup(rates => rates.GetRateAsync("USD", secondaryCode, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CurrencyRateQuote(
                    rate,
                    CurrencyRateSource.Manual,
                    Core.Helpers.TimeZoneHelper.GetVenezuelaDate(),
                    "USD",
                    secondaryCode));
        }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
        }
    }

    private static Mock<IMonetaryProfileService> CreateProfile(
        MonetaryMode mode,
        string primaryCode,
        string? secondaryCode = null)
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = mode,
                PrimaryCurrencyCode = primaryCode,
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = secondaryCode,
                SecondaryCurrencySymbol = secondaryCode,
                IsConfigured = true
            });
        return profile;
    }

    private static async Task SeedPaymentMethodsAsync(SalesDbContext context)
    {
        // El seed del modelo crea "Cash" (Id=1); se fija como gaveta legacy Bs.S para que la
        // seleccion local por moneda sea observable frente a las gavetas USD y COP.
        var legacyCash = await context.PaymentMethods.FirstAsync(method => method.Id == 1);
        legacyCash.Name = "Efectivo Bs.S";
        legacyCash.Currency = "Bs.S";
        legacyCash.IsCash = true;
        legacyCash.IsActive = true;
        legacyCash.IsDeleted = false;

        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 11, Name = "Efectivo USD", Currency = "USD", IsCash = true, IsActive = true, DisplayOrder = 11 },
            new PaymentMethod { Id = 12, Name = "Efectivo COP", Currency = "COP", IsCash = true, IsActive = true, DisplayOrder = 12 },
            new PaymentMethod { Id = 13, Name = "Punto de Venta", Currency = "COP", IsCash = false, IsActive = true, DisplayOrder = 13 });

        await context.SaveChangesAsync();
    }

    private static async Task<CashDrawerSession> SeedOpenSessionAsync(
        SalesDbContext context,
        decimal openingLocal,
        decimal openingUsd = 0m)
    {
        var session = new CashDrawerSession
        {
            OpenedAt = DateTime.UtcNow,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = openingLocal,
            OpeningExchangeRate = BcvRate,
            OpeningBalanceUsd = openingUsd
        };
        context.CashDrawerSessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    private static PettyCashExpenseRequest Request(
        PettyCashCurrency currency = PettyCashCurrency.BsS,
        decimal amount = 250m) => new()
        {
            Currency = currency,
            Amount = amount,
            Category = PettyCashCategory.InsumosLimpieza,
            Concept = "Compra operativa",
            Beneficiary = "Proveedor de prueba",
            ReferenceNumber = "REF-PC-001"
        };

    [Fact]
    public async Task SingleCop_LocalExpense_UsesCopMethodAndWritesCopSnapshotsAndReversal()
    {
        var profile = CreateProfile(MonetaryMode.Single, "COP");
        using var stack = new PettyCashStack(profile.Object);
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m, openingUsd: 100m);

        var expense = await stack.Service.RegisterAsync(
            Request(PettyCashCurrency.BsS, 250m),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(1m, expense.HistoricalRate);
        Assert.Equal(250m, expense.AmountBsS);
        Assert.Equal(250m, expense.AmountUsd);

        var movement = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal("COP", movement.CurrencyCode);
        Assert.Equal(12, movement.PaymentMethodId);
        Assert.Equal(1m, movement.ExchangeRate);
        Assert.Equal(250m, movement.AmountLocal);
        Assert.Equal(750m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));

        await stack.Service.CancelAsync(expense.Id, "Anulado para prueba", actingUserId: 1, actingRole: UserRole.Admin);

        var reversal = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashReversal);
        Assert.Equal("COP", reversal.CurrencyCode);
        Assert.Equal(12, reversal.PaymentMethodId);
        Assert.Equal(1m, reversal.ExchangeRate);

        stack.Inventory.Verify(
            inventory => inventory.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SingleCop_UsdExpense_IsRejectedBecauseUsdIsNotInTheProfile()
    {
        var profile = CreateProfile(MonetaryMode.Single, "COP");
        using var stack = new PettyCashStack(profile.Object);
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m, openingUsd: 100m);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => stack.Service.RegisterAsync(
            Request(PettyCashCurrency.Usd, 10m),
            actingUserId: 1,
            actingRole: UserRole.Admin));

        Assert.Contains("USD no forma parte del perfil monetario", exception.Message);
        Assert.Contains("COP", exception.Message);
        Assert.Empty(await stack.Context.PettyCashExpenses.AsNoTracking().ToListAsync());
        Assert.Empty(await stack.Context.CashTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DualUsdCop_LocalExpense_UsesCopMethodAndFreezesTheManualSnapshot()
    {
        var profile = CreateProfile(MonetaryMode.Dual, "USD", "COP");
        using var stack = new PettyCashStack(profile.Object);
        stack.ConfigureManualRate("COP", ManualCopRate);
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 200_000m, openingUsd: 200m);

        var expense = await stack.Service.RegisterAsync(
            Request(PettyCashCurrency.BsS, 1000m),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(4000.1235m, expense.HistoricalRate);
        Assert.Equal(1000m, expense.AmountBsS);
        Assert.Equal(0.2500m, expense.AmountUsd);

        var movement = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal("COP", movement.CurrencyCode);
        Assert.Equal(12, movement.PaymentMethodId);
        Assert.Equal(4000.1235m, movement.ExchangeRate);
        Assert.Equal(199_000m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));

        await stack.Service.CancelAsync(expense.Id, "Anulado para prueba", actingUserId: 1, actingRole: UserRole.Admin);
        var reversal = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashReversal);
        Assert.Equal("COP", reversal.CurrencyCode);
        Assert.Equal(12, reversal.PaymentMethodId);
        Assert.Equal(4000.1235m, reversal.ExchangeRate);

        stack.Inventory.Verify(
            inventory => inventory.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DualUsdCop_UsdExpense_ValuatesWith8dpAndSnapshots4dp()
    {
        var profile = CreateProfile(MonetaryMode.Dual, "USD", "COP");
        using var stack = new PettyCashStack(profile.Object);
        stack.ConfigureManualRate("COP", ManualCopRate);
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 200_000m, openingUsd: 200m);

        var expense = await stack.Service.RegisterAsync(
            Request(PettyCashCurrency.Usd, 116m),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        decimal expectedLocal = Math.Round(116m * ManualCopRate, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(464_014.32m, expectedLocal);
        Assert.Equal(expectedLocal, expense.AmountBsS);
        Assert.Equal(116.0000m, expense.AmountUsd);
        Assert.Equal(4000.1235m, expense.HistoricalRate);

        var movement = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal("USD", movement.CurrencyCode);
        Assert.Equal(11, movement.PaymentMethodId);
        Assert.Equal(4000.1235m, movement.ExchangeRate);
        Assert.Equal(84m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd));
    }

    [Fact]
    public async Task DualUsdVes_LocalExpense_KeepsBsSAndTheLegacyBcvCeiling()
    {
        var profile = CreateProfile(MonetaryMode.Dual, "USD", "VES");
        using var stack = new PettyCashStack(profile.Object, bcvRate: 36.555m);
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m, openingUsd: 100m);

        var expense = await stack.Service.RegisterAsync(
            Request(PettyCashCurrency.BsS, 365.50m),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(36.56m, expense.HistoricalRate);

        var movement = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal("Bs.S", movement.CurrencyCode);
        Assert.Equal(1, movement.PaymentMethodId);
        Assert.Equal(36.56m, movement.ExchangeRate);

        stack.Inventory.Verify(
            inventory => inventory.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WithoutProfile_LocalExpense_KeepsLegacyBsSBehavior()
    {
        using var stack = new PettyCashStack(profile: null);
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var expense = await stack.Service.RegisterAsync(
            Request(PettyCashCurrency.BsS, 100m),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(BcvRate, expense.HistoricalRate);

        var movement = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal("Bs.S", movement.CurrencyCode);
        Assert.Equal(1, movement.PaymentMethodId);
    }
}
