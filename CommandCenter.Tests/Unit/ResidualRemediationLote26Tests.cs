using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ResidualRemediationLote26Tests
{
    [Fact]
    public async Task ReserveStockAsync_WhenReservationInsertFails_RollsBackReservedQuantity()
    {
        var (db, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (db)
        {
            db.Products.Add(new Product
            {
                Id = 1,
                SKU = "RES-ATOMIC-01",
                Name = "Reserva Atomica",
                StockQuantity = 10m,
                ReservedQuantity = 0m,
                PriceRetailUSD = 5.00m,
                CostPriceUSD = 2.00m
            });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER TR_StockReservations_FailInsert BEFORE INSERT ON StockReservations BEGIN SELECT RAISE(ABORT, 'forced reservation failure'); END;");

            var service = new InventoryService(db);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                service.ReserveStockAsync(1, 3m, TimeSpan.FromMinutes(10)));

            db.ChangeTracker.Clear();

            var persisted = await db.Products.AsNoTracking().SingleAsync(p => p.Id == 1);
            Assert.Equal(0m, persisted.ReservedQuantity);
            Assert.Empty(await db.StockReservations.AsNoTracking().ToListAsync());
        }
    }

    [Fact]
    public async Task ReserveStockAsync_Relational_CommitsIncrementAndReservationInOneUnit()
    {
        var (db, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (db)
        {
            db.Products.Add(new Product
            {
                Id = 1,
                SKU = "RES-ATOMIC-02",
                Name = "Reserva Atomica 2",
                StockQuantity = 10m,
                ReservedQuantity = 0m,
                PriceRetailUSD = 5.00m,
                CostPriceUSD = 2.00m
            });
            await db.SaveChangesAsync();

            var service = new InventoryService(db);

            var reservationId = await service.ReserveStockAsync(1, 3m, TimeSpan.FromMinutes(10));

            Assert.True(reservationId > 0);

            db.ChangeTracker.Clear();

            var persisted = await db.Products.AsNoTracking().SingleAsync(p => p.Id == 1);
            Assert.Equal(3m, persisted.ReservedQuantity);

            var reservation = await db.StockReservations.AsNoTracking().SingleAsync(r => r.Id == reservationId);
            Assert.Equal(3m, reservation.Quantity);
        }
    }

    [Fact]
    public async Task ReserveStockAsync_WithAmbientTransaction_RollsBackWithTheEnclosingUnitOfWork()
    {
        var (db, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (db)
        {
            db.Products.Add(new Product
            {
                Id = 1,
                SKU = "RES-ATOMIC-03",
                Name = "Reserva Atomica 3",
                StockQuantity = 10m,
                ReservedQuantity = 0m,
                PriceRetailUSD = 5.00m,
                CostPriceUSD = 2.00m
            });
            await db.SaveChangesAsync();

            var service = new InventoryService(db);

            await using (var ambient = await db.Database.BeginTransactionAsync())
            {
                var reservationId = await service.ReserveStockAsync(1, 3m, TimeSpan.FromMinutes(10));
                Assert.True(reservationId > 0);

                db.ChangeTracker.Clear();
                Assert.Equal(3m, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == 1)).ReservedQuantity);

                await ambient.RollbackAsync();
            }

            db.ChangeTracker.Clear();

            var persisted = await db.Products.AsNoTracking().SingleAsync(p => p.Id == 1);
            Assert.Equal(0m, persisted.ReservedQuantity);
            Assert.Empty(await db.StockReservations.AsNoTracking().ToListAsync());
        }
    }

    [Fact]
    public async Task CompleteSale_WithNullPayments_MapsEmptyCollectionAndReturnsOk()
    {
        var mockSalesService = new Mock<ISalesService>();
        IEnumerable<PaymentInfo>? capturedPayments = null;
        mockSalesService
            .Setup(s => s.CompleteSaleAsync(
                It.IsAny<int>(),
                It.IsAny<decimal>(),
                It.IsAny<IEnumerable<PaymentInfo>>(),
                It.IsAny<decimal>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<byte[]?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>()))
            .Callback<int, decimal, IEnumerable<PaymentInfo>, decimal, int?, bool, string?, byte[]?, CancellationToken, int?>(
                (_, _, payments, _, _, _, _, _, _, _) => capturedPayments = payments)
            .ReturnsAsync(321);

        var controller = CreateSalesController(mockSalesService);
        var request = new CompleteSaleRequest { ExchangeRate = 45m, Payments = null! };

        var response = await controller.CompleteSale(1, request);

        var ok = Assert.IsType<OkObjectResult>(response);
        Assert.Equal(321, ok.Value);
        Assert.NotNull(capturedPayments);
        Assert.Empty(capturedPayments);
    }

    [Fact]
    public async Task CompleteSale_WithNullPaymentElement_DoesNotThrowNullReference()
    {
        var mockSalesService = new Mock<ISalesService>();
        IEnumerable<PaymentInfo>? capturedPayments = null;
        mockSalesService
            .Setup(s => s.CompleteSaleAsync(
                It.IsAny<int>(),
                It.IsAny<decimal>(),
                It.IsAny<IEnumerable<PaymentInfo>>(),
                It.IsAny<decimal>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<byte[]?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>()))
            .Callback<int, decimal, IEnumerable<PaymentInfo>, decimal, int?, bool, string?, byte[]?, CancellationToken, int?>(
                (_, _, payments, _, _, _, _, _, _, _) => capturedPayments = payments)
            .ReturnsAsync(321);

        var controller = CreateSalesController(mockSalesService);
        var request = new CompleteSaleRequest
        {
            ExchangeRate = 45m,
            Payments = new List<SalePaymentDto> { null! }
        };

        var response = await controller.CompleteSale(1, request);

        Assert.IsType<OkObjectResult>(response);
        Assert.NotNull(capturedPayments);
        Assert.Empty(capturedPayments);
    }

    [Fact]
    public async Task CompleteSale_WhenDomainRejectsEmptyPayments_SurfacesDomainExceptionNotNullReference()
    {
        var mockSalesService = new Mock<ISalesService>();
        mockSalesService
            .Setup(s => s.CompleteSaleAsync(
                It.IsAny<int>(),
                It.IsAny<decimal>(),
                It.IsAny<IEnumerable<PaymentInfo>>(),
                It.IsAny<decimal>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<byte[]?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>()))
            .ThrowsAsync(new InvalidOperationException("No se puede completar la venta sin pagos registrados."));

        var controller = CreateSalesController(mockSalesService);
        var request = new CompleteSaleRequest { ExchangeRate = 45m, Payments = null! };

        var exception = await Record.ExceptionAsync(() => controller.CompleteSale(1, request));

        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task CreateClosureAsync_WithDuplicatedPaymentMethodIds_ThrowsArgumentException()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = DailyClosureTestHelper.CreateService(context);

        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin",
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 100m },
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 50m }
            }
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateClosureAsync(closure));

        Assert.Contains("duplicados", exception.Message);
        Assert.Contains("1", exception.Message);
        Assert.Empty(await context.DailyClosures.ToListAsync());
    }

    [Fact]
    public async Task DailyClosureController_WithDuplicatedPaymentMethodIds_ReturnsBadRequestWithoutCreatingClosure()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        if (!await salesDb.PaymentMethods.AnyAsync(p => p.Id == 1))
        {
            salesDb.PaymentMethods.Add(new PaymentMethod
            {
                Id = 1,
                Name = "Efectivo USD",
                IsCash = true,
                IsActive = true,
                IsDeleted = false,
                DisplayOrder = 1
            });
            await salesDb.SaveChangesAsync();
        }

        var (rateProvider, cashDrawer) = DailyClosureTestHelper.CreateMocks();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var closureService = DailyClosureTestHelper.CreateService(salesDb, rateProvider, cashDrawer);

        var controller = new DailyClosureController(
            closureService,
            mockUser.Object)
        {
            ControllerContext = CreateAdminControllerContext()
        };

        var request = new CreateClosureRequest
        {
            Details = new List<CreateClosureDetailRequest>
            {
                new CreateClosureDetailRequest { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 100m },
                new CreateClosureDetailRequest { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 50m }
            }
        };

        var result = await controller.CreateClosure(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
        Assert.Empty(await salesDb.DailyClosures.AsNoTracking().ToListAsync());
        cashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task ShiftsController_WithDuplicatedDeclaredPaymentMethodIds_ReturnsBadRequestWithoutCreatingClosure()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        if (!await salesDb.PaymentMethods.AnyAsync(p => p.Id == 2))
        {
            salesDb.PaymentMethods.Add(new PaymentMethod
            {
                Id = 2,
                Name = "Efectivo Bs.S",
                IsCash = true,
                IsActive = true,
                IsDeleted = false,
                DisplayOrder = 2
            });
            await salesDb.SaveChangesAsync();
        }

        var (rateProvider, cashDrawer) = DailyClosureTestHelper.CreateMocks();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var closureService = DailyClosureTestHelper.CreateService(salesDb, rateProvider, cashDrawer);

        var controller = new ShiftsController(
            closureService,
            mockUser.Object)
        {
            ControllerContext = CreateAdminControllerContext()
        };

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new DeclaredAmountDto { PaymentMethodId = 2, Amount = 100m },
                new DeclaredAmountDto { PaymentMethodId = 2, Amount = 250m }
            }
        };

        var result = await controller.CloseShiftAsync(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
        Assert.Empty(await salesDb.DailyClosures.AsNoTracking().ToListAsync());
        cashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()), Times.Never);
    }

    private static SalesController CreateSalesController(Mock<ISalesService> salesService)
    {
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Idempotency-Key"] = "LOTE26-NULL-PAYMENTS";
        httpContext.Request.Path = "/api/sales/1/complete";
        httpContext.Request.Method = "POST";

        return new SalesController(salesService.Object, mockUser.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static ControllerContext CreateAdminControllerContext()
    {
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    new[]
                    {
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1"),
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")
                    },
                    "TestAuth"))
            }
        };
    }
}
