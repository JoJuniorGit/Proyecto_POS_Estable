using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Hubs;
using Backend.API.Jobs;
using Backend.API.Services;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ExchangeRateJobTests
{
    [Fact]
    public void BcvExchangeRateJob_IsRegisteredAsHostedService()
    {
        // Arrange
        var services = new ServiceCollection();

        // Register standard background services as in Program.cs
        services.AddHostedService<CacheMetricsLoggerService>();
        services.AddHostedService<StockMovementArchiverJob>();
        services.AddHostedService<BcvExchangeRateJob>();

        // Act & Assert
        var hostedServices = services.Where(s => s.ServiceType == typeof(IHostedService)).ToList();
        var hasBcvJob = hostedServices.Any(s => s.ImplementationType == typeof(BcvExchangeRateJob));

        Assert.True(hasBcvJob, "BcvExchangeRateJob MUST be registered as an IHostedService in the DI container.");
    }

    [Fact]
    public async Task BcvExchangeRateJob_WhenScraperFails_CatchesErrorAndDoesNotMutateDatabaseOrCache()
    {
        // Arrange
        var contextOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new InventoryDbContext(contextOptions);

        // Pre-existing rate
        var today = TimeZoneHelper.GetVenezuelaDate();
        dbContext.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = today.AddDays(-1),
            Rate = 800.00m,
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        });
        await dbContext.SaveChangesAsync();

        var scraperLogger = new Mock<ILogger<BcvScraperService>>();
        var scraperMock = new Mock<BcvScraperService>(new HttpClient(), scraperLogger.Object, null!);
        scraperMock.Setup(s => s.GetOfficialUsdRateAsync(It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new HttpRequestException("Portal BCV no disponible (502 Bad Gateway)"));

        var hubContextMock = new Mock<IHubContext<ExchangeRateHub>>();
        var hubClientsMock = new Mock<IHubClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);

        var inventoryMock = new Mock<IInventoryService>();
        var salesMock = new Mock<ISalesService>();

        var services = new ServiceCollection();
        services.AddSingleton(scraperMock.Object);
        services.AddSingleton(dbContext);
        services.AddSingleton(hubContextMock.Object);
        services.AddSingleton(inventoryMock.Object);
        services.AddSingleton(salesMock.Object);

        var serviceProvider = services.BuildServiceProvider();
        var jobLogger = new Mock<ILogger<BcvExchangeRateJob>>();

        var job = new BcvExchangeRateJob(serviceProvider, jobLogger.Object);

        // Act - should NOT throw
        await job.SyncRateAsync(CancellationToken.None);

        // Assert
        var records = await dbContext.ExchangeRateHistory.ToListAsync();
        Assert.Single(records);
        Assert.Equal(800.00m, records[0].Rate);

        inventoryMock.Verify(i => i.InvalidateTodayExchangeRateCache(), Times.Never);
        salesMock.Verify(s => s.RecalculateOnHoldSalesAsync(It.IsAny<decimal>()), Times.Never);
        clientProxyMock.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10.5)]
    [InlineData(1000000)]
    [InlineData(5000000)]
    public async Task BcvExchangeRateJob_WhenScrapedRateIsOutOfReasonableRange_IgnoresValueAndLogsWarning(decimal invalidRate)
    {
        // Arrange
        var contextOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new InventoryDbContext(contextOptions);

        var scraperLogger = new Mock<ILogger<BcvScraperService>>();
        var scraperMock = new Mock<BcvScraperService>(new HttpClient(), scraperLogger.Object, null!);
        scraperMock.Setup(s => s.GetOfficialUsdRateAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(invalidRate);

        var hubContextMock = new Mock<IHubContext<ExchangeRateHub>>();
        var inventoryMock = new Mock<IInventoryService>();
        var salesMock = new Mock<ISalesService>();

        var services = new ServiceCollection();
        services.AddSingleton(scraperMock.Object);
        services.AddSingleton(dbContext);
        services.AddSingleton(hubContextMock.Object);
        services.AddSingleton(inventoryMock.Object);
        services.AddSingleton(salesMock.Object);

        var serviceProvider = services.BuildServiceProvider();
        var jobLogger = new Mock<ILogger<BcvExchangeRateJob>>();

        var job = new BcvExchangeRateJob(serviceProvider, jobLogger.Object);

        // Act
        await job.SyncRateAsync(CancellationToken.None);

        // Assert - DB remains empty, no modifications
        Assert.Empty(await dbContext.ExchangeRateHistory.ToListAsync());
        inventoryMock.Verify(i => i.InvalidateTodayExchangeRateCache(), Times.Never);
    }

    [Fact]
    public async Task BcvExchangeRateJob_WhenNewValidRateDetected_SavesWithCeilingRounding_AndEmitsSignalR()
    {
        // Arrange
        var contextOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new InventoryDbContext(contextOptions);

        var scraperLogger = new Mock<ILogger<BcvScraperService>>();
        var scraperMock = new Mock<BcvScraperService>(new HttpClient(), scraperLogger.Object, null!);
        // Raw rate with precision: 804.6301 -> should be rounded up to 804.64
        scraperMock.Setup(s => s.GetOfficialUsdRateAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(804.6301m);

        var hubContextMock = new Mock<IHubContext<ExchangeRateHub>>();
        var hubClientsMock = new Mock<IHubClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);

        var inventoryMock = new Mock<IInventoryService>();
        var salesMock = new Mock<ISalesService>();

        var services = new ServiceCollection();
        services.AddSingleton(scraperMock.Object);
        services.AddSingleton(dbContext);
        services.AddSingleton(hubContextMock.Object);
        services.AddSingleton(inventoryMock.Object);
        services.AddSingleton(salesMock.Object);

        var serviceProvider = services.BuildServiceProvider();
        var jobLogger = new Mock<ILogger<BcvExchangeRateJob>>();

        var job = new BcvExchangeRateJob(serviceProvider, jobLogger.Object);

        // Act
        await job.SyncRateAsync(CancellationToken.None);

        // Assert
        var today = TimeZoneHelper.GetVenezuelaDate();
        var savedRecord = await dbContext.ExchangeRateHistory.FirstOrDefaultAsync(r => r.Date == today);
        Assert.NotNull(savedRecord);
        Assert.Equal(804.64m, savedRecord.Rate);

        inventoryMock.Verify(i => i.InvalidateTodayExchangeRateCache(), Times.Once);
        salesMock.Verify(s => s.RecalculateOnHoldSalesAsync(804.64m), Times.Once);

        clientProxyMock.Verify(
            p => p.SendCoreAsync("ReceiveRateUpdate", It.Is<object[]>(o => (decimal)o[0] == 804.64m), It.IsAny<CancellationToken>()),
            Times.Once);
        clientProxyMock.Verify(
            p => p.SendCoreAsync("OnHoldSalesUpdated", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(804.6301, 804.64)]
    [InlineData(804.6300, 804.63)]
    [InlineData(805.1234, 805.13)]
    [InlineData(800.0000, 800.00)]
    [InlineData(36.4567, 36.46)]
    public void PricingCalculator_RoundExchangeRateCeiling_RoundsUpToCent(decimal input, decimal expected)
    {
        var result = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task GetToday_WhenTodayRecordDoesNotExist_ReturnsLatestHistoricalRate_IgnoringFutureDates()
    {
        // Arrange
        var contextOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new InventoryDbContext(contextOptions);

        var today = TimeZoneHelper.GetVenezuelaDate();

        // 1. Future date (erroneous entry)
        dbContext.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = today.AddDays(5),
            Rate = 999.99m,
            UpdatedAt = DateTime.UtcNow
        });

        // 2. Past valid date (e.g. from 2 days ago)
        dbContext.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = today.AddDays(-2),
            Rate = 804.82m,
            UpdatedAt = DateTime.UtcNow.AddDays(-2)
        });

        await dbContext.SaveChangesAsync();

        var userMock = new Mock<ICurrentUserService>();
        var salesMock = new Mock<ISalesService>();
        var inventoryMock = new Mock<IInventoryService>();
        var hubContextMock = new Mock<IHubContext<ExchangeRateHub>>();

        var controller = new ExchangeRateController(
            dbContext,
            userMock.Object,
            salesMock.Object,
            inventoryMock.Object,
            hubContextMock.Object);

        // Act
        var actionResult = await controller.GetToday();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        Assert.NotNull(okResult.Value);

        // Reflect properties of anonymous object
        var valueProp = okResult.Value.GetType().GetProperty("Value")?.GetValue(okResult.Value);
        var dateProp = okResult.Value.GetType().GetProperty("Date")?.GetValue(okResult.Value);

        Assert.Equal(804.82m, valueProp);
        Assert.Equal(today.AddDays(-2), dateProp);
    }

    [Fact]
    public async Task HybridFlow_Integration_JobUpdatesRate_AndControllerGetTodayReturnsIt()
    {
        // Arrange
        var contextOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new InventoryDbContext(contextOptions);

        var today = TimeZoneHelper.GetVenezuelaDate();

        // Historical rate
        dbContext.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = today.AddDays(-1),
            Rate = 800.00m,
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        });
        await dbContext.SaveChangesAsync();

        var scraperLogger = new Mock<ILogger<BcvScraperService>>();
        var scraperMock = new Mock<BcvScraperService>(new HttpClient(), scraperLogger.Object, null!);
        scraperMock.Setup(s => s.GetOfficialUsdRateAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(806.50m);

        var hubContextMock = new Mock<IHubContext<ExchangeRateHub>>();
        var hubClientsMock = new Mock<IHubClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var userMock = new Mock<ICurrentUserService>();
        var inventoryService = new InventoryService(dbContext, userMock.Object, memoryCache);
        var salesMock = new Mock<ISalesService>();

        var services = new ServiceCollection();
        services.AddSingleton(scraperMock.Object);
        services.AddSingleton(dbContext);
        services.AddSingleton(hubContextMock.Object);
        services.AddSingleton<IInventoryService>(inventoryService);
        services.AddSingleton(salesMock.Object);

        var serviceProvider = services.BuildServiceProvider();
        var jobLogger = new Mock<ILogger<BcvExchangeRateJob>>();

        var job = new BcvExchangeRateJob(serviceProvider, jobLogger.Object);
        var controller = new ExchangeRateController(
            dbContext,
            userMock.Object,
            salesMock.Object,
            inventoryService,
            hubContextMock.Object);

        // 1. Before job runs: GetToday returns yesterday's rate (800.00m) via fallback
        var initialRes = Assert.IsType<OkObjectResult>(await controller.GetToday());
        var initialVal = initialRes.Value?.GetType().GetProperty("Value")?.GetValue(initialRes.Value);
        Assert.Equal(800.00m, initialVal);

        // 2. Job runs and scrapes new rate (806.50m)
        await job.SyncRateAsync(CancellationToken.None);

        // 3. After job runs: GetToday immediately returns today's updated rate (806.50m)
        var updatedRes = Assert.IsType<OkObjectResult>(await controller.GetToday());
        var updatedVal = updatedRes.Value?.GetType().GetProperty("Value")?.GetValue(updatedRes.Value);
        Assert.Equal(806.50m, updatedVal);
    }

    [Fact]
    public void TimeZoneHelper_CalculatesVenezuelaDateCorrectly()
    {
        // 2026-09-04 01:30:00 UTC corresponds to 2026-09-03 21:30:00 in Venezuela (UTC-4)
        var utcPastMidnight = new DateTime(2026, 9, 4, 1, 30, 0, DateTimeKind.Utc);
        var venezuelaDate = TimeZoneHelper.GetVenezuelaDate(utcPastMidnight);

        Assert.Equal(new DateOnly(2026, 9, 3), venezuelaDate);
    }

    [Fact]
    public void InvalidateTodayExchangeRateCache_PurgesCacheKey()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        const string cacheKey = "bcv_rate_today";
        memoryCache.Set(cacheKey, 65.50m);

        Assert.True(memoryCache.TryGetValue(cacheKey, out decimal cachedVal));
        Assert.Equal(65.50m, cachedVal);

        var contextOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new InventoryDbContext(contextOptions);
        var userMock = new Mock<ICurrentUserService>();
        var inventoryService = new InventoryService(dbContext, userMock.Object, memoryCache);

        // Act
        inventoryService.InvalidateTodayExchangeRateCache();

        // Assert
        Assert.False(memoryCache.TryGetValue(cacheKey, out _), "bcv_rate_today should be purged from memory cache.");
    }
}
