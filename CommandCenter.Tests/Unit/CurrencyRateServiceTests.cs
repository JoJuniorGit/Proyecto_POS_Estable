using System;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T2a (S4): resolución de tasas por par. USD/VES se alimenta exclusivamente de
// ExchangeRateHistory (BCV, techo 2dp, fallback al último registro <= fecha); el resto de pares
// se carga manual por fecha exacta y el inverso se deriva a 8dp. La identidad no toca la base.
public class CurrencyRateServiceTests
{
    private const string MalformedFromMessage = "El código de la moneda origen debe ser un código ISO 4217 de tres letras.";
    private const string MalformedToMessage = "El código de la moneda destino debe ser un código ISO 4217 de tres letras.";
    private const string SameCodeMessage = "El par de monedas debe tener códigos distintos.";
    private const string BcvPairMessage = "El par USD/VES se alimenta de la tasa BCV y no admite carga manual.";
    private const string InvalidRateMessage = "La tasa debe ser mayor a cero y menor o igual a 1.000.000.";
    private const string FutureDateMessage = "La fecha de la tasa no puede ser futura.";
    private const string UnsupportedZzzMessage = "El código 'ZZZ' no es un código ISO 4217 reconocido.";

    private static readonly DateOnly RateDate = new(2026, 10, 20);

    private static InventoryDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private static CurrencyRateService CreateService(InventoryDbContext db, IMemoryCache? cache = null)
        => new(db, cache);

    private static ManualCurrencyRateRequest CreateRequest(
        string from,
        string to,
        decimal rate,
        DateOnly? date = null)
        => new()
        {
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = rate,
            RateDate = date
        };

    // ---------------------------------------------------------------------------------------------
    // Identidad.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetRateAsync_SameCurrency_ReturnsIdentityWithoutTouchingTheDatabase()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var quote = await service.GetRateAsync("usd", "USD", RateDate);

        Assert.Equal(1m, quote.Rate);
        Assert.Equal(CurrencyRateSource.Identity, quote.Source);
        Assert.Equal("USD", quote.FromCurrencyCode);
        Assert.Equal("USD", quote.ToCurrencyCode);
        Assert.Equal(RateDate, quote.RateDate);
        Assert.Equal(0, await db.CurrencyPairRates.CountAsync());
        Assert.Equal(0, await db.ExchangeRateHistory.CountAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // BCV (USD/VES).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetRateAsync_LocalAliases_ReturnIdentityWithoutDatabase()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var quote = await service.GetRateAsync("Bs.S", "VES", RateDate);

        Assert.Equal(1m, quote.Rate);
        Assert.Equal(CurrencyRateSource.Identity, quote.Source);
        Assert.Equal("VES", quote.FromCurrencyCode);
        Assert.Equal("VES", quote.ToCurrencyCode);
        Assert.Equal(0, await db.ExchangeRateHistory.CountAsync());
    }

    [Fact]
    public async Task GetRateAsync_UsdToVes_UsesExactDateAndCeilingToTwoDecimals()
    {
        using var db = CreateInMemoryDb();
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = RateDate.AddDays(-1), Rate = 10m });
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = RateDate, Rate = 36.123m });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var quote = await service.GetRateAsync("usd", "ves", RateDate);

        Assert.Equal(36.13m, quote.Rate);
        Assert.Equal(CurrencyRateSource.Bcv, quote.Source);
        Assert.Equal("USD", quote.FromCurrencyCode);
        Assert.Equal("VES", quote.ToCurrencyCode);
        Assert.Equal(RateDate, quote.RateDate);
    }

    [Fact]
    public async Task GetRateAsync_UsdToVes_WhenExactDateMissing_FallsBackToLatestPriorRecord()
    {
        using var db = CreateInMemoryDb();
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = RateDate.AddDays(-2), Rate = 10m });
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = RateDate.AddDays(-1), Rate = 40.501m });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var quote = await service.GetRateAsync("USD", "VES", RateDate);

        Assert.Equal(40.51m, quote.Rate);
        Assert.Equal(CurrencyRateSource.Bcv, quote.Source);
    }

    [Fact]
    public async Task GetRateAsync_VesToUsd_ReturnsBcvInverseAtEightDecimals()
    {
        using var db = CreateInMemoryDb();
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = RateDate, Rate = 36.123m });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var quote = await service.GetRateAsync("VES", "USD", RateDate);

        var expected = Math.Round(1m / 36.13m, 8, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, quote.Rate);
        Assert.Equal(CurrencyRateSource.BcvInverse, quote.Source);
        Assert.Equal("VES", quote.FromCurrencyCode);
        Assert.Equal("USD", quote.ToCurrencyCode);
    }

    [Fact]
    public async Task GetRateAsync_UsdToLocalAlias_UsesBcvAndReturnsCanonicalVesCode()
    {
        using var db = CreateInMemoryDb();
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = RateDate, Rate = 36.123m });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var quote = await service.GetRateAsync("USD", "Bs.S", RateDate);

        Assert.Equal(36.13m, quote.Rate);
        Assert.Equal(CurrencyRateSource.Bcv, quote.Source);
        Assert.Equal("USD", quote.FromCurrencyCode);
        Assert.Equal("VES", quote.ToCurrencyCode);
    }

    [Fact]
    public async Task GetRateAsync_LocalAliasToUsd_ReturnsBcvInverseWithCanonicalVesCode()
    {
        using var db = CreateInMemoryDb();
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = RateDate, Rate = 36.123m });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var quote = await service.GetRateAsync("Bs.S", "USD", RateDate);

        var expected = Math.Round(1m / 36.13m, 8, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, quote.Rate);
        Assert.Equal(CurrencyRateSource.BcvInverse, quote.Source);
        Assert.Equal("VES", quote.FromCurrencyCode);
        Assert.Equal("USD", quote.ToCurrencyCode);
    }

    [Fact]
    public async Task GetRateAsync_UsdToVes_WithoutRecord_ThrowsExactMessage()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetRateAsync("USD", "VES", RateDate));

        Assert.Equal($"No hay tasa BCV registrada para la fecha {RateDate:yyyy-MM-dd}.", exception.Message);
    }

    [Fact]
    public async Task GetRateAsync_UsdToVes_WithNonPositiveRecord_ThrowsExactMessage()
    {
        using var db = CreateInMemoryDb();
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = RateDate, Rate = 0m });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetRateAsync("USD", "VES", RateDate));

        Assert.Equal($"No hay tasa BCV registrada para la fecha {RateDate:yyyy-MM-dd}.", exception.Message);
    }

    [Fact]
    public async Task GetRateAsync_UsdToVes_WhenExactDateIsNonPositive_FallsBackToLatestPriorPositive()
    {
        using var db = CreateInMemoryDb();
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = RateDate.AddDays(-1), Rate = 30.123m });
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = RateDate, Rate = 0m });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var quote = await service.GetRateAsync("USD", "VES", RateDate);

        Assert.Equal(30.13m, quote.Rate);
        Assert.Equal(CurrencyRateSource.Bcv, quote.Source);
        Assert.Equal(RateDate, quote.RateDate);
    }

    // ---------------------------------------------------------------------------------------------
    // Pares manuales.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetRateAsync_ManualExactPair_ReturnsManualSource()
    {
        using var db = CreateInMemoryDb();
        db.CurrencyPairRates.Add(new CurrencyPairRate
        {
            RateDate = RateDate,
            FromCurrencyCode = "EUR",
            ToCurrencyCode = "COP",
            Rate = 4200.12345678m,
            UpdatedByUserId = 5
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var quote = await service.GetRateAsync("eur", "cop", RateDate);

        Assert.Equal(4200.12345678m, quote.Rate);
        Assert.Equal(CurrencyRateSource.Manual, quote.Source);
        Assert.Equal("EUR", quote.FromCurrencyCode);
        Assert.Equal("COP", quote.ToCurrencyCode);
        Assert.Equal(RateDate, quote.RateDate);
    }

    [Fact]
    public async Task GetRateAsync_ManualInverse_ReturnsInverseAtEightDecimals()
    {
        using var db = CreateInMemoryDb();
        db.CurrencyPairRates.Add(new CurrencyPairRate
        {
            RateDate = RateDate,
            FromCurrencyCode = "EUR",
            ToCurrencyCode = "COP",
            Rate = 4200.12345678m
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var quote = await service.GetRateAsync("COP", "EUR", RateDate);

        var expected = Math.Round(1m / 4200.12345678m, 8, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, quote.Rate);
        Assert.Equal(CurrencyRateSource.ManualInverse, quote.Source);
        Assert.Equal("COP", quote.FromCurrencyCode);
        Assert.Equal("EUR", quote.ToCurrencyCode);
    }

    [Fact]
    public async Task GetRateAsync_ManualPairWithoutRecord_ThrowsExactMessage()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetRateAsync("EUR", "COP", RateDate));

        Assert.Equal(
            $"No hay tasa manual configurada para EUR->COP en la fecha {RateDate:yyyy-MM-dd}.",
            exception.Message);
    }

    [Fact]
    public async Task GetRateAsync_ManualPairOnlyResolvesTheExactDate_WithoutFallback()
    {
        using var db = CreateInMemoryDb();
        db.CurrencyPairRates.Add(new CurrencyPairRate
        {
            RateDate = RateDate.AddDays(-1),
            FromCurrencyCode = "EUR",
            ToCurrencyCode = "COP",
            Rate = 4200m
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetRateAsync("EUR", "COP", RateDate));

        Assert.Equal(
            $"No hay tasa manual configurada para EUR->COP en la fecha {RateDate:yyyy-MM-dd}.",
            exception.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // SetManualRateAsync.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetManualRateAsync_NormalizesRoundsPersistsAndRoundTrips()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var saved = await service.SetManualRateAsync(
            CreateRequest(" eur ", "cop", 1234.123456789m, RateDate),
            updatedByUserId: 7);

        Assert.Equal(1234.12345679m, saved.Rate);
        Assert.Equal(CurrencyRateSource.Manual, saved.Source);
        Assert.Equal("EUR", saved.FromCurrencyCode);
        Assert.Equal("COP", saved.ToCurrencyCode);
        Assert.Equal(RateDate, saved.RateDate);

        var row = await db.CurrencyPairRates.AsNoTracking().SingleAsync();
        Assert.Equal("EUR", row.FromCurrencyCode);
        Assert.Equal("COP", row.ToCurrencyCode);
        Assert.Equal(1234.12345679m, row.Rate);
        Assert.Equal(RateDate, row.RateDate);
        Assert.Equal(7, row.UpdatedByUserId);

        var fetched = await service.GetRateAsync("EUR", "COP", RateDate);
        Assert.Equal(saved.Rate, fetched.Rate);
        Assert.Equal(CurrencyRateSource.Manual, fetched.Source);
    }

    [Fact]
    public async Task SetManualRateAsync_SamePairAndDate_UpsertsASingleRow()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        await service.SetManualRateAsync(CreateRequest("EUR", "COP", 100m, RateDate), updatedByUserId: 1);
        await service.SetManualRateAsync(CreateRequest("EUR", "COP", 200m, RateDate), updatedByUserId: 2);

        Assert.Equal(1, await db.CurrencyPairRates.CountAsync());
        var row = await db.CurrencyPairRates.AsNoTracking().SingleAsync();
        Assert.Equal(200m, row.Rate);
        Assert.Equal(2, row.UpdatedByUserId);
    }

    [Fact]
    public async Task SetManualRateAsync_LocalAliasToEur_PersistsCanonicalVesPair()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var saved = await service.SetManualRateAsync(
            CreateRequest("Bs.S", "EUR", 0.85m, RateDate),
            updatedByUserId: 3);

        Assert.Equal("VES", saved.FromCurrencyCode);
        Assert.Equal("EUR", saved.ToCurrencyCode);
        Assert.Equal(0.85m, saved.Rate);

        var row = await db.CurrencyPairRates.AsNoTracking().SingleAsync();
        Assert.Equal("VES", row.FromCurrencyCode);
        Assert.Equal("EUR", row.ToCurrencyCode);

        var fetched = await service.GetRateAsync("Bs.S", "EUR", RateDate);
        Assert.Equal(0.85m, fetched.Rate);
        Assert.Equal("VES", fetched.FromCurrencyCode);
        Assert.Equal("EUR", fetched.ToCurrencyCode);
    }

    [Fact]
    public async Task SetManualRateAsync_WithoutRateDate_UsesVenezuelaToday()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var saved = await service.SetManualRateAsync(CreateRequest("EUR", "COP", 100m), updatedByUserId: null);

        var today = TimeZoneHelper.GetVenezuelaDate();
        Assert.Equal(today, saved.RateDate);
        var row = await db.CurrencyPairRates.AsNoTracking().SingleAsync();
        Assert.Equal(today, row.RateDate);
    }

    public static TheoryData<string, string, decimal, string, string> InvalidRequests => new()
    {
        { "usd", "USD", 1m, "2026-10-20", SameCodeMessage },
        { "USD", "VES", 36m, "2026-10-20", BcvPairMessage },
        { "VES", "USD", 0.03m, "2026-10-20", BcvPairMessage },
        { "Bs.S", "USD", 36m, "2026-10-20", BcvPairMessage },
        { "EUR", "COP", 0m, "2026-10-20", InvalidRateMessage },
        { "EUR", "COP", -1m, "2026-10-20", InvalidRateMessage },
        { "EUR", "COP", 1_000_000.01m, "2026-10-20", InvalidRateMessage },
        { "EU", "COP", 100m, "2026-10-20", MalformedFromMessage },
        { "EUR", "C0P", 100m, "2026-10-20", MalformedToMessage },
        { "ZZZ", "COP", 100m, "2026-10-20", UnsupportedZzzMessage }
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task SetManualRateAsync_InvalidRequest_ThrowsExactMessageAndPersistsNothing(
        string from,
        string to,
        decimal rate,
        string rateDate,
        string expectedMessage)
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);
        var request = CreateRequest(from, to, rate, DateOnly.Parse(rateDate));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetManualRateAsync(request, updatedByUserId: 1));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Equal(0, await db.CurrencyPairRates.CountAsync());
    }

    [Fact]
    public async Task SetManualRateAsync_WithRateThatRoundsToZero_ThrowsExactMessageAndPersistsNothing()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetManualRateAsync(
                CreateRequest("EUR", "COP", 0.000000001m, RateDate),
                updatedByUserId: 1));

        Assert.Equal(InvalidRateMessage, exception.Message);
        Assert.Equal(0, await db.CurrencyPairRates.CountAsync());
    }

    [Fact]
    public async Task SetManualRateAsync_FutureDate_ThrowsExactMessageAndPersistsNothing()
    {
        using var db = CreateInMemoryDb();
        var service = CreateService(db);
        var futureDate = TimeZoneHelper.GetVenezuelaDate().AddDays(1);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetManualRateAsync(CreateRequest("EUR", "COP", 100m, futureDate), updatedByUserId: 1));

        Assert.Equal(FutureDateMessage, exception.Message);
        Assert.Equal(0, await db.CurrencyPairRates.CountAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // GetManualRatesAsync.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetManualRatesAsync_FiltersByDateAndOrdersByFromThenTo()
    {
        using var db = CreateInMemoryDb();
        db.CurrencyPairRates.AddRange(
            new CurrencyPairRate { RateDate = RateDate, FromCurrencyCode = "VES", ToCurrencyCode = "EUR", Rate = 1m },
            new CurrencyPairRate { RateDate = RateDate, FromCurrencyCode = "EUR", ToCurrencyCode = "COP", Rate = 2m },
            new CurrencyPairRate { RateDate = RateDate, FromCurrencyCode = "EUR", ToCurrencyCode = "ARS", Rate = 3m },
            new CurrencyPairRate { RateDate = RateDate.AddDays(-1), FromCurrencyCode = "AUD", ToCurrencyCode = "GBP", Rate = 4m });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var rates = await service.GetManualRatesAsync(RateDate);

        Assert.Equal(3, rates.Count);
        Assert.Collection(
            rates,
            rate => Assert.Equal(("EUR", "ARS"), (rate.FromCurrencyCode, rate.ToCurrencyCode)),
            rate => Assert.Equal(("EUR", "COP"), (rate.FromCurrencyCode, rate.ToCurrencyCode)),
            rate => Assert.Equal(("VES", "EUR"), (rate.FromCurrencyCode, rate.ToCurrencyCode)));
        Assert.All(rates, rate => Assert.Equal(RateDate, rate.RateDate));
    }

    [Fact]
    public async Task GetManualRatesAsync_WithoutDate_UsesVenezuelaToday()
    {
        using var db = CreateInMemoryDb();
        var today = TimeZoneHelper.GetVenezuelaDate();
        db.CurrencyPairRates.AddRange(
            new CurrencyPairRate { RateDate = today, FromCurrencyCode = "EUR", ToCurrencyCode = "COP", Rate = 2m },
            new CurrencyPairRate { RateDate = today.AddDays(-1), FromCurrencyCode = "AUD", ToCurrencyCode = "GBP", Rate = 4m });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var rates = await service.GetManualRatesAsync();

        var rate = Assert.Single(rates);
        Assert.Equal("EUR", rate.FromCurrencyCode);
        Assert.Equal(today, rate.RateDate);
    }

    // ---------------------------------------------------------------------------------------------
    // Caché (invalidación explícita al reescribir el par).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetRateAsync_ManualQuote_CacheIsInvalidatedWhenRateIsOverwritten()
    {
        using var db = CreateInMemoryDb();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
        var service = CreateService(db, cache);

        await service.SetManualRateAsync(CreateRequest("EUR", "COP", 100m, RateDate), updatedByUserId: 1);
        var first = await service.GetRateAsync("EUR", "COP", RateDate);
        Assert.Equal(100m, first.Rate);

        await service.SetManualRateAsync(CreateRequest("EUR", "COP", 200m, RateDate), updatedByUserId: 1);
        var second = await service.GetRateAsync("EUR", "COP", RateDate);

        Assert.Equal(200m, second.Rate);
        Assert.Equal(CurrencyRateSource.Manual, second.Source);
    }

    [Fact]
    public async Task GetRateAsync_ManualInverseCache_IsInvalidatedWhenForwardRateIsOverwritten()
    {
        using var db = CreateInMemoryDb();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
        var service = CreateService(db, cache);

        await service.SetManualRateAsync(CreateRequest("EUR", "COP", 100m, RateDate), updatedByUserId: 1);
        var first = await service.GetRateAsync("COP", "EUR", RateDate);
        Assert.Equal(0.01m, first.Rate);

        await service.SetManualRateAsync(CreateRequest("EUR", "COP", 400m, RateDate), updatedByUserId: 1);
        var second = await service.GetRateAsync("COP", "EUR", RateDate);

        Assert.Equal(0.0025m, second.Rate);
        Assert.Equal(CurrencyRateSource.ManualInverse, second.Source);
    }
}
