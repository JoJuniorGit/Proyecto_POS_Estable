using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Exceptions;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests;

[Trait("Category", "RequiresDocker")]
public class HoldOrderClaimTestsPostgres
{
    private static SalesService CreateService(SalesDbContext context)
    {
        return new SalesService(
            context,
            new Mock<IInventoryService>().Object,
            new Mock<IMediator>().Object,
            new Mock<ICashDrawerService>().Object,
            new Mock<ISystemSettingsService>().Object,
            new Mock<IHoldOrderNotifier>().Object);
    }

    private static async Task<Exception?> TryClaimAsync(SalesService service, int saleId, int userId)
    {
        try
        {
            await service.ClaimSaleAsync(saleId, SaleClaimAction.Editing, userId);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task ClaimSaleAsync_TwoConcurrentClaims_ExactlyOneWins()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr)) return;

        const int saleId = 987654;
        const int firstUserId = 9001;
        const int secondUserId = 9002;

        using (var seedContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext())
        {
            if (seedContext == null) return;

            var existing = await seedContext.Sales.FirstOrDefaultAsync(s => s.Id == saleId);
            if (existing != null)
            {
                seedContext.Sales.Remove(existing);
                await seedContext.SaveChangesAsync();
            }

            seedContext.Sales.Add(new Sale
            {
                Id = saleId,
                Status = SaleStatus.OnHold,
                TotalUSD = 100m,
                AppliedRate = 50m
            });
            await seedContext.SaveChangesAsync();
        }

        using var firstContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        using var secondContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        if (firstContext == null || secondContext == null) return;

        var firstService = CreateService(firstContext);
        var secondService = CreateService(secondContext);

        var results = await Task.WhenAll(
            TryClaimAsync(firstService, saleId, firstUserId),
            TryClaimAsync(secondService, saleId, secondUserId));

        Assert.Equal(1, results.Count(r => r == null));
        Assert.Equal(1, results.Count(r => r is SaleLockedException));
    }
}
