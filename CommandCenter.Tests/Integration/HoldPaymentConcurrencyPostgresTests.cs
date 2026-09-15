using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class HoldPaymentConcurrencyPostgresTests
{
    private const int SaleId = 987656;
    private const int ActorId = 9100;

    private static SalesService CreateService(SalesDbContext context) =>
        new(context,
            new Mock<IInventoryService>().Object,
            new Mock<IMediator>().Object,
            new Mock<ICashDrawerService>().Object,
            new Mock<ISystemSettingsService>().Object);

    private static async Task<Exception?> TryAddPaymentAsync(SalesService service, AddPaymentRequestDto request)
    {
        try
        {
            await service.AddPaymentToHoldSaleAsync(SaleId, request, actingUserId: ActorId);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task AddPaymentToHoldSaleAsync_TwoConcurrentAbonos_CannotOverpaySale()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr)) return;

        using (var seedContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext())
        {
            if (seedContext == null) return;
            await TestDatabaseFactory.SeedStandardSalesDataAsync(seedContext);

            await seedContext.SalePayments.Where(p => p.SaleId == SaleId).ExecuteDeleteAsync();
            await seedContext.Sales.Where(s => s.Id == SaleId).ExecuteDeleteAsync();

            seedContext.Sales.Add(new Sale
            {
                Id = SaleId,
                Status = SaleStatus.OnHold,
                TotalUSD = 100m,
                AppliedRate = 50m,
                ClaimedByUserId = ActorId,
                ClaimAction = SaleClaimAction.Editing,
                ClaimedByUserName = "Concurrency Actor",
                ClaimedAtUtc = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        try
        {
            using var firstContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
            using var secondContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
            if (firstContext == null || secondContext == null) return;

            var paymentMethodId = await firstContext.PaymentMethods
                .Where(pm => !pm.IsCash && pm.IsActive)
                .Select(pm => pm.Id)
                .FirstAsync();

            var request = new AddPaymentRequestDto
            {
                PaymentMethodId = paymentMethodId,
                AmountUSD = 60m,
                AmountBsS = 3000m,
                ExchangeRate = 50m
            };

            var results = await Task.WhenAll(
                TryAddPaymentAsync(CreateService(firstContext), request),
                TryAddPaymentAsync(CreateService(secondContext), request));

            Assert.Equal(1, results.Count(r => r == null));
            Assert.Equal(1, results.Count(r => r is ArgumentException));

            using var verifyContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
            if (verifyContext == null) return;

            decimal paid = await verifyContext.SalePayments
                .Where(p => p.SaleId == SaleId)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            Assert.True(paid <= 100.05m, $"El total abonado ({paid}) no debe exceder el total de la venta.");
        }
        finally
        {
            using var cleanupContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
            if (cleanupContext != null)
            {
                await cleanupContext.SalePayments.Where(p => p.SaleId == SaleId).ExecuteDeleteAsync();
                await cleanupContext.Sales.Where(s => s.Id == SaleId).ExecuteDeleteAsync();
            }
        }
    }
}
