using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Hubs;
using Core.Entities;
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
using Moq;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ExchangeRateJobRemovalTests
{
    [Fact]
    public void BcvExchangeRateJob_IsNotRegisteredAsHostedService()
    {
        // Arrange
        var services = new ServiceCollection();

        // Register standard services resembling Program.cs
        services.AddHostedService<Backend.API.Services.CacheMetricsLoggerService>();

        // Act & Assert
        var hostedServices = services.Where(s => s.ServiceType == typeof(IHostedService)).ToList();
        var hasBcvJob = hostedServices.Any(s => s.ImplementationType?.Name == "BcvExchangeRateJob");

        Assert.False(hasBcvJob, "BcvExchangeRateJob must NOT be registered as an IHostedService in the DI container.");
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

    [Fact]
    public async Task UpsertRate_BroadcastsSignalRUpdate_AndInvalidatesCache()
    {
        // Arrange
        var contextOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new InventoryDbContext(contextOptions);

        var userMock = new Mock<ICurrentUserService>();
        userMock.Setup(u => u.CanMutateExchangeRate).Returns(true);

        var salesMock = new Mock<ISalesService>();
        var inventoryMock = new Mock<IInventoryService>();

        // Setup SignalR mocks
        var hubContextMock = new Mock<IHubContext<ExchangeRateHub>>();
        var hubClientsMock = new Mock<IHubClients>();
        var clientProxyMock = new Mock<IClientProxy>();

        hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);

        var controller = new ExchangeRateController(
            dbContext,
            userMock.Object,
            salesMock.Object,
            inventoryMock.Object,
            hubContextMock.Object);

        var request = new UpsertExchangeRateRequest { Value = 68.25m };

        // Act
        var result = await controller.UpsertRate(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        // Verify cache invalidation was called
        inventoryMock.Verify(i => i.InvalidateTodayExchangeRateCache(), Times.Once);

        // Verify OnHold sales recalculation was called
        salesMock.Verify(s => s.RecalculateOnHoldSalesAsync(68.25m), Times.Once);

        // Verify SignalR broadcast called ReceiveRateUpdate and OnHoldSalesUpdated
        clientProxyMock.Verify(
            p => p.SendCoreAsync("ReceiveRateUpdate", It.Is<object[]>(o => (decimal)o[0] == 68.25m), default),
            Times.Once);

        clientProxyMock.Verify(
            p => p.SendCoreAsync("OnHoldSalesUpdated", It.IsAny<object[]>(), default),
            Times.Once);
    }
}
