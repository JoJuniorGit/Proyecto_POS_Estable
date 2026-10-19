using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Integration;
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

/// <summary>
/// Claim concurrente de ventas en espera contra PostgreSQL real en base AISLADA por test (carril
/// <see cref="PostgresTestLane"/>). Sin conexión utilizable el carril falla EXPLÍCITO (nunca
/// silent-pass) y la base apuntada por la cadena base no se escribe.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
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
        await using var database = await IsolatedPostgresDatabase.CreateAsync();

        const int saleId = 987654;
        const int firstUserId = 9001;
        const int secondUserId = 9002;

        await using (var seedContext = database.CreateSalesDbContext())
        {
            seedContext.Sales.Add(new Sale
            {
                Id = saleId,
                Status = SaleStatus.OnHold,
                TotalUSD = 100m,
                AppliedRate = 50m
            });
            await seedContext.SaveChangesAsync();
        }

        await using var firstContext = database.CreateSalesDbContext();
        await using var secondContext = database.CreateSalesDbContext();

        var firstService = CreateService(firstContext);
        var secondService = CreateService(secondContext);

        var results = await Task.WhenAll(
            TryClaimAsync(firstService, saleId, firstUserId),
            TryClaimAsync(secondService, saleId, secondUserId));

        Assert.Equal(1, results.Count(r => r == null));
        Assert.Equal(1, results.Count(r => r is SaleLockedException));
    }
}
