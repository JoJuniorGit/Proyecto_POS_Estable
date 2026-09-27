using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class DailyClosureTransactionTests
{
    private static (SalesDbContext Context, SqliteConnection Connection) CreateSqliteContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new SalesDbContext(options);
        context.Database.EnsureCreated();
        return (context, connection);
    }

    private static async Task SeedPaymentMethodAsync(SalesDbContext context, int id)
    {
        if (!await context.PaymentMethods.AnyAsync(p => p.Id == id))
        {
            context.PaymentMethods.Add(new PaymentMethod
            {
                Id = id,
                Name = "Efectivo USD",
                IsCash = true,
                IsActive = true,
                IsDeleted = false,
                DisplayOrder = id
            });
            await context.SaveChangesAsync();
        }
    }

    private static CreateClosureCommand CreateCommand()
        => new(
            ClosureDateUtc: DateTime.UtcNow,
            UserId: "Admin",
            Observation: "V-00000000",
            Declarations: new List<DeclaredPaymentAmount> { new(1, 100m) });

    [Fact]
    public async Task CreateClosureFromCommandAsync_RunsInsideSerializableTransaction()
    {
        var (context, connection) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            await SeedPaymentMethodAsync(context, 1);
            var (rateProvider, _) = DailyClosureTestHelper.CreateMocks();

            bool transactionPresentDuringRollover = false;
            IsolationLevel? isolationLevelDuringRollover = null;
            var cashDrawer = new Mock<ICashDrawerService>();
            cashDrawer.Setup(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()))
                .Callback(() =>
                {
                    transactionPresentDuringRollover = context.Database.CurrentTransaction is not null;
                    isolationLevelDuringRollover = context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel;
                })
                .Returns(Task.CompletedTask);

            var service = new DailyClosureService(context, rateProvider.Object, cashDrawer.Object);

            var result = await service.CreateClosureFromCommandAsync(CreateCommand(), CancellationToken.None);

            Assert.True(transactionPresentDuringRollover);
            Assert.Equal(IsolationLevel.Serializable, isolationLevelDuringRollover);
            Assert.True(result.ClosureId > 0);
            Assert.Equal(1, await context.DailyClosures.AsNoTracking().CountAsync());
            cashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()), Times.Once);
        }
    }

    [Fact]
    public async Task CreateClosureFromCommandAsync_WhenRolloverFails_RollsBackThePersistedClosure()
    {
        var (context, connection) = CreateSqliteContext();
        using (connection)
        using (context)
        {
            await SeedPaymentMethodAsync(context, 1);
            var (rateProvider, _) = DailyClosureTestHelper.CreateMocks();

            bool transactionPresentDuringRollover = false;
            var cashDrawer = new Mock<ICashDrawerService>();
            cashDrawer.Setup(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()))
                .Callback(() => transactionPresentDuringRollover = context.Database.CurrentTransaction is not null)
                .ThrowsAsync(new InvalidOperationException("rollover failure"));

            var service = new DailyClosureService(context, rateProvider.Object, cashDrawer.Object);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.CreateClosureFromCommandAsync(CreateCommand(), CancellationToken.None));

            context.ChangeTracker.Clear();
            int persistedClosures = await context.DailyClosures.AsNoTracking().CountAsync();

            Assert.Equal(0, persistedClosures);
            Assert.True(transactionPresentDuringRollover);
        }
    }
}
