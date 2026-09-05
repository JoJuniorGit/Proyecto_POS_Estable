using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase2IntegrityRemediationTests
{
    private SalesDbContext CreateInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SalesDbContext(options);
    }

    private InventoryDbContext CreateInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task PaymentMethod_Delete_WhenUsedInCashTransactions_AppliesSoftDelete()
    {
        // Arrange
        using var context = CreateInMemorySalesDbContext();
        var pm = new PaymentMethod
        {
            Id = 10,
            Name = "Efectivo Especial",
            IsCash = true,
            IsActive = true,
            IsDeleted = false
        };
        context.PaymentMethods.Add(pm);

        var cashTx = new CashTransaction
        {
            Id = 501,
            SessionId = 1,
            PaymentMethodId = 10,
            AmountUsd = 20m,
            AmountLocal = 1000m,
            IsPhysicalCash = true,
            Description = "Abono con Efectivo Especial"
        };
        context.CashTransactions.Add(cashTx);
        await context.SaveChangesAsync();

        var service = new PaymentMethodService(context);

        // Act
        await service.DeleteAsync(10);

        // Assert: Must be soft deleted (IsDeleted = true, IsActive = false), not removed physically
        var inDb = await context.PaymentMethods.FindAsync(10);
        Assert.NotNull(inDb);
        Assert.True(inDb.IsDeleted);
        Assert.False(inDb.IsActive);
    }

    [Fact]
    public async Task CashAdvance_AsDriver_Returns403Forbidden()
    {
        // Arrange
        using var db = CreateInMemorySalesDbContext();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("4");

        var controller = new CashDrawerController(mockCashDrawer.Object, mockSettings.Object, db, mockUser.Object);

        var userClaims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "4"),
            new Claim(ClaimTypes.Role, "Driver")
        }, "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = userClaims }
        };

        var request = new CashAdvanceRequest
        {
            SessionId = 1,
            RequestedAmountLocal = 500m,
            PaymentMethodId = 1,
            PaymentMethodName = "Pago Móvil",
            IsTransfer = false,
            ExchangeRate = 50m
        };

        // Act
        var result = await controller.ProcessCashAdvance(request);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        mockCashDrawer.Verify(c => c.ProcessCashAdvanceAsync(
            It.IsAny<int>(),
            It.IsAny<decimal>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<decimal>(),
            It.IsAny<int?>(),
            It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task DailyClosure_BackdatingByAdminBeyond24Hours_ReturnsBadRequest()
    {
        // Arrange
        using var salesDb = CreateInMemorySalesDbContext();
        using var inventoryDb = CreateInMemoryInventoryDbContext();
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = new DailyClosureController(
            mockClosure.Object, 
            mockCashDrawer.Object, 
            inventoryDb, 
            mockSettings.Object, 
            salesDb, 
            mockUser.Object);

        var adminClaims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "Admin")
        }, "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = adminClaims }
        };

        var request = new CreateClosureRequest
        {
            ClosureDate = DateTime.UtcNow.AddHours(-26), // > 24h ago
            UserId = "1",
            Details = new List<CreateClosureDetailRequest>()
        };

        // Act
        var result = await controller.CreateClosure(request);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task StockMovementArchiverJob_ArchivesAndDeletesOldRecords()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        var oldDate = DateTime.UtcNow.AddDays(-400);

        using (var seedContext = new InventoryDbContext(options))
        {
            seedContext.StockMovements.AddRange(
                new StockMovement { Id = 1, ProductId = 101, QuantityChange = -2, NewStockLevel = 8, Reason = "Sale #1", MovementDate = oldDate },
                new StockMovement { Id = 2, ProductId = 101, QuantityChange = -3, NewStockLevel = 5, Reason = "Sale #2", MovementDate = oldDate },
                new StockMovement { Id = 3, ProductId = 102, QuantityChange = 10, NewStockLevel = 20, Reason = "Purchase", MovementDate = DateTime.UtcNow } // Recent
            );
            await seedContext.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<InventoryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var serviceProvider = services.BuildServiceProvider();

        var inMemoryConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Archiver:RetentionDays", "365" },
                { "Archiver:IntervalHours", "24" }
            })
            .Build();

        var job = new Backend.API.Jobs.StockMovementArchiverJob(
            serviceProvider,
            NullLogger<Backend.API.Jobs.StockMovementArchiverJob>.Instance,
            inMemoryConfig);

        // Use reflection to invoke private ArchiveOldRecordsAsync
        var method = typeof(Backend.API.Jobs.StockMovementArchiverJob)
            .GetMethod("ArchiveOldRecordsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        // Act
        var task = (Task)method.Invoke(job, new object[] { CancellationToken.None })!;
        await task;

        // Assert using fresh context
        using (var verifyContext = new InventoryDbContext(options))
        {
            var remainingMovements = await verifyContext.StockMovements.ToListAsync();
            Assert.Single(remainingMovements);
            Assert.Equal(3, remainingMovements[0].Id); // Only recent movement remains

            var archivedRecords = await verifyContext.StockMovements_Archive.ToListAsync();
            Assert.Equal(2, archivedRecords.Count);
            Assert.Contains(archivedRecords, a => a.OriginalMovementId == 1 && a.Reason == "Sale #1");
            Assert.Contains(archivedRecords, a => a.OriginalMovementId == 2 && a.Reason == "Sale #2");
        }
    }
}
