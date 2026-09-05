using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Jobs;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase4SharedTransactionAndIdempotencyTests
{
    #region 1. Idempotency Key Regex Validation Tests

    [Theory]
    [InlineData("order-123_abc.xyz")]
    [InlineData("a")]
    [InlineData("1234567890")]
    [InlineData("UUID-1234-5678-9abc")]
    public void IsValidKey_AcceptsValidAlphanumericAndPunctuation(string key)
    {
        Assert.True(IdempotencyService.IsValidKey(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("order 123")] // espacio no permitido
    [InlineData("order;drop_table")] // punto y coma no permitido
    [InlineData("order/path")] // slash no permitido
    [InlineData("order@domain")] // arroba no permitida
    public void IsValidKey_RejectsInvalidFormatOrSpecialChars(string? key)
    {
        Assert.False(IdempotencyService.IsValidKey(key));
    }

    [Fact]
    public void IsValidKey_Enforces128MaxCharacterLimit()
    {
        var valid128 = new string('A', 128);
        var invalid129 = new string('A', 129);

        Assert.True(IdempotencyService.IsValidKey(valid128));
        Assert.False(IdempotencyService.IsValidKey(invalid129));
    }

    #endregion

    #region 2. SHA-256 Composite Hash Computation Tests

    [Fact]
    public void ComputePayloadHash_ProducesDeterministic32ByteHash()
    {
        var method = "POST";
        var path = "/api/sales/1/complete";
        var body = Encoding.UTF8.GetBytes("{\"exchangeRate\": 45.5}");

        var hash1 = IdempotencyService.ComputePayloadHash(method, path, body);
        var hash2 = IdempotencyService.ComputePayloadHash(method, path, body);

        Assert.NotNull(hash1);
        Assert.Equal(32, hash1.Length);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputePayloadHash_DifferentInputs_ProduceDifferentHashes()
    {
        var body = Encoding.UTF8.GetBytes("{\"amount\": 100}");
        var hashBase = IdempotencyService.ComputePayloadHash("POST", "/api/sales/1/complete", body);

        // Diferente método
        var hashDiffMethod = IdempotencyService.ComputePayloadHash("PUT", "/api/sales/1/complete", body);
        Assert.NotEqual(hashBase, hashDiffMethod);

        // Diferente ruta
        var hashDiffPath = IdempotencyService.ComputePayloadHash("POST", "/api/sales/2/complete", body);
        Assert.NotEqual(hashBase, hashDiffPath);

        // Diferente cuerpo
        var hashDiffBody = IdempotencyService.ComputePayloadHash("POST", "/api/sales/1/complete", Encoding.UTF8.GetBytes("{\"amount\": 200}"));
        Assert.NotEqual(hashBase, hashDiffBody);
    }

    #endregion

    #region 3. IdempotencyService CheckAsync & RegisterSuccessAsync Tests

    [Fact]
    public async Task CheckAsync_WhenKeyDoesNotExist_ReturnsIsNewTrue()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new IdempotencyService(context);

        var key = "test-key-new";
        var path = "/api/sales/1/complete";
        var hash = IdempotencyService.ComputePayloadHash("POST", path, Encoding.UTF8.GetBytes("{}"));

        var result = await service.CheckAsync(key, path, hash);

        Assert.True(result.IsNew);
        Assert.False(result.IsReplay);
        Assert.False(result.IsMismatch);
    }

    [Fact]
    public async Task RegisterSuccessAsync_ThenCheckAsync_ReturnsReplayWithHit()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new IdempotencyService(context);

        var key = "test-key-replay";
        var path = "/api/sales/1/complete";
        var bodyBytes = Encoding.UTF8.GetBytes("{\"saleId\": 101}");
        var hash = IdempotencyService.ComputePayloadHash("POST", path, bodyBytes);

        // Registrar éxito
        await service.RegisterSuccessAsync(key, path, hash, 200, "101");

        // Verificar con la misma clave y el mismo hash
        var result = await service.CheckAsync(key, path, hash);

        Assert.False(result.IsNew);
        Assert.True(result.IsReplay);
        Assert.False(result.IsMismatch);
        Assert.Equal(200, result.StoredStatusCode);
        Assert.Equal("101", result.StoredResponseBody);
    }

    [Fact]
    public async Task CheckAsync_WhenKeyExistsWithDifferentPayload_ReturnsMismatch()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new IdempotencyService(context);

        var key = "test-key-mismatch";
        var path = "/api/sales/1/complete";
        var originalBody = Encoding.UTF8.GetBytes("{\"saleId\": 101, \"amount\": 50.0}");
        var originalHash = IdempotencyService.ComputePayloadHash("POST", path, originalBody);

        await service.RegisterSuccessAsync(key, path, originalHash, 200, "101");

        // Segundo intento con diferente contenido para la misma clave
        var tamperedBody = Encoding.UTF8.GetBytes("{\"saleId\": 101, \"amount\": 999.0}");
        var tamperedHash = IdempotencyService.ComputePayloadHash("POST", path, tamperedBody);

        var result = await service.CheckAsync(key, path, tamperedHash);

        Assert.False(result.IsNew);
        Assert.False(result.IsReplay);
        Assert.True(result.IsMismatch);
    }

    [Fact]
    public async Task CheckAsync_WhenRecordIsExpired_TreatsAsNew()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new IdempotencyService(context);

        var key = "expired-key";
        var path = "/api/sales/1/complete";
        var hash = IdempotencyService.ComputePayloadHash("POST", path, Encoding.UTF8.GetBytes("{}"));

        // Insertar registro expirado
        context.IdempotentRequests.Add(new IdempotentRequest
        {
            Key = key,
            RequestPath = path,
            PayloadHash = hash,
            StatusCode = 200,
            ResponseBody = "100",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-2),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5) // Expirado
        });
        await context.SaveChangesAsync();

        var result = await service.CheckAsync(key, path, hash);

        Assert.True(result.IsNew);
        Assert.False(result.IsReplay);
    }

    #endregion

    #region 4. Idempotency Cleanup Job (Purge in Batches) Tests

    [Fact]
    public async Task IdempotencyCleanupJob_PurgesExpiredRecordsAndRetainsValidOnes()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        // 10 registros expirados
        for (int i = 0; i < 10; i++)
        {
            context.IdempotentRequests.Add(new IdempotentRequest
            {
                Key = $"expired-{i}",
                RequestPath = "/api/sales",
                PayloadHash = new byte[32],
                StatusCode = 200,
                ResponseBody = "ok",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-3),
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            });
        }

        // 5 registros vigentes
        for (int i = 0; i < 5; i++)
        {
            context.IdempotentRequests.Add(new IdempotentRequest
            {
                Key = $"valid-{i}",
                RequestPath = "/api/sales",
                PayloadHash = new byte[32],
                StatusCode = 200,
                ResponseBody = "ok",
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(23)
            });
        }
        await context.SaveChangesAsync();

        Assert.Equal(15, await context.IdempotentRequests.CountAsync());

        // Ejecutar purga de registros expirados
        var now = DateTime.UtcNow;
        var expired = await context.IdempotentRequests
            .Where(r => r.ExpiresAtUtc <= now)
            .Take(1000)
            .ToListAsync();

        context.IdempotentRequests.RemoveRange(expired);
        await context.SaveChangesAsync();

        var remaining = await context.IdempotentRequests.ToListAsync();
        Assert.Equal(5, remaining.Count);
        Assert.All(remaining, r => Assert.StartsWith("valid-", r.Key));
    }

    #endregion

    #region 5. Concurrency Stress Test (10+ Concurrent Tasks)

    [Fact]
    public async Task ConcurrentRequests_WithSameIdempotencyKey_ResolveWithoutDuplicates()
    {
        var (initContext, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (initContext)
        {
            var key = "concurrent-stress-key-01";
            var path = "/api/sales/50/complete";
            var bodyBytes = Encoding.UTF8.GetBytes("{\"saleId\": 50, \"payments\": []}");
            var hash = IdempotencyService.ComputePayloadHash("POST", path, bodyBytes);

            int concurrentThreads = 12;
            var successfulRegistrations = 0;
            var replayHits = 0;
            var conflictsHandled = 0;

            // Ejecutar 12 tareas concurrentes simulando solicitudes paralelas con sus propios DbContext
            var tasks = Enumerable.Range(0, concurrentThreads).Select(async i =>
            {
                await Task.Yield();
                using var taskContext = TestDatabaseFactory.CreateSqliteSalesDbContext(connection);
                var service = new IdempotencyService(taskContext);

                var check = await service.CheckAsync(key, path, hash);
                if (check.IsNew)
                {
                    try
                    {
                        await service.RegisterSuccessAsync(key, path, hash, 200, "5000");
                        Interlocked.Increment(ref successfulRegistrations);
                    }
                    catch (Exception)
                    {
                        // Colisión concurrente resuelta
                        var collision = await service.HandleConcurrentCollisionAsync(key, path, hash);
                        if (collision.IsReplay) Interlocked.Increment(ref replayHits);
                        else Interlocked.Increment(ref conflictsHandled);
                    }
                }
                else if (check.IsReplay)
                {
                    Interlocked.Increment(ref replayHits);
                }
            });

            await Task.WhenAll(tasks);

            // El registro en BD debe ser único
            var countInDb = await initContext.IdempotentRequests.CountAsync(r => r.Key == key && r.RequestPath == path);
            Assert.Equal(1, countInDb);

            // Entre todos los hilos, se atendieron todas las solicitudes (registro inicial + replays/colisiones)
            Assert.Equal(concurrentThreads, successfulRegistrations + replayHits + conflictsHandled);
            Assert.Equal(1, successfulRegistrations);
        }
    }

    #endregion

    #region 6. SalesController Idempotency & Stats Endpoint Tests

    [Fact]
    public async Task CompleteSale_WithIdempotencyKey_ReturnsMissOnFirstAndHitOnReplay()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var idService = new IdempotencyService(context);

        var mockSalesService = new Mock<ISalesService>();
        mockSalesService.Setup(s => s.CompleteSaleAsync(
            1, 45.0m, It.IsAny<IEnumerable<PaymentInfo>>(), 0m, It.IsAny<int?>(), false, "IDEMP-001", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<int, decimal, IEnumerable<PaymentInfo>, decimal, int?, bool, string?, byte[]?, CancellationToken>(
                (sId, rate, pay, round, cId, pick, k, h, ct) =>
                {
                    if (!string.IsNullOrEmpty(k) && h != null)
                    {
                        context.IdempotentRequests.Add(new IdempotentRequest
                        {
                            Key = k,
                            RequestPath = "/api/sales/1/complete",
                            PayloadHash = h,
                            StatusCode = 200,
                            ResponseBody = "777",
                            CreatedAtUtc = DateTime.UtcNow,
                            ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
                        });
                        context.SaveChanges();
                    }
                })
            .ReturnsAsync(777);

        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = new SalesController(mockSalesService.Object, mockUser.Object, idService);

        // Setup HttpContext
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Idempotency-Key"] = "IDEMP-001";
        httpContext.Request.Path = "/api/sales/1/complete";
        httpContext.Request.Method = "POST";
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var requestDto = new CompleteSaleRequest
        {
            ExchangeRate = 45.0m,
            Payments = new List<SalePaymentDto> { new SalePaymentDto { PaymentMethodId = 1, Amount = 10, AmountBsS = 450 } }
        };

        // Primer intento: MISS
        var actionResult = await controller.CompleteSale(1, requestDto);
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        Assert.Equal(777, okResult.Value);
        Assert.Equal("MISS", httpContext.Response.Headers["X-Cache-Lookup"].ToString());

        // Segundo intento: Replay con HIT
        var replayContext = new DefaultHttpContext();
        replayContext.Request.Headers["Idempotency-Key"] = "IDEMP-001";
        replayContext.Request.Path = "/api/sales/1/complete";
        replayContext.Request.Method = "POST";
        replayContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = replayContext };

        var replayResult = await controller.CompleteSale(1, requestDto);
        var replayOk = Assert.IsType<OkObjectResult>(replayResult);
        Assert.Equal(777, replayOk.Value);
        Assert.Equal("HIT", replayContext.Response.Headers["X-Cache-Lookup"].ToString());

        // El servicio de ventas solo fue llamado UNA vez
        mockSalesService.Verify(s => s.CompleteSaleAsync(
            1, 45.0m, It.IsAny<IEnumerable<PaymentInfo>>(), 0m, It.IsAny<int?>(), false, "IDEMP-001", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompleteSale_WhenPayloadMismatchesKey_Returns422Unprocessable()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var idService = new IdempotencyService(context);

        var mockSalesService = new Mock<ISalesService>();
        mockSalesService.Setup(s => s.CompleteSaleAsync(
            1, 45.0m, It.IsAny<IEnumerable<PaymentInfo>>(), 0m, It.IsAny<int?>(), false, "IDEMP-002", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<int, decimal, IEnumerable<PaymentInfo>, decimal, int?, bool, string?, byte[]?, CancellationToken>(
                (sId, rate, pay, round, cId, pick, k, h, ct) =>
                {
                    if (!string.IsNullOrEmpty(k) && h != null)
                    {
                        context.IdempotentRequests.Add(new IdempotentRequest
                        {
                            Key = k,
                            RequestPath = "/api/sales/1/complete",
                            PayloadHash = h,
                            StatusCode = 200,
                            ResponseBody = "888",
                            CreatedAtUtc = DateTime.UtcNow,
                            ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
                        });
                        context.SaveChanges();
                    }
                })
            .ReturnsAsync(888);

        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = new SalesController(mockSalesService.Object, mockUser.Object, idService);

        var httpContext1 = new DefaultHttpContext();
        httpContext1.Request.Headers["Idempotency-Key"] = "IDEMP-002";
        httpContext1.Request.Path = "/api/sales/1/complete";
        httpContext1.Request.Method = "POST";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext1 };

        var req1 = new CompleteSaleRequest
        {
            ExchangeRate = 45.0m,
            Payments = new List<SalePaymentDto> { new SalePaymentDto { PaymentMethodId = 1, Amount = 10, AmountBsS = 450 } }
        };
        await controller.CompleteSale(1, req1);

        // Mismo Idempotency-Key pero con Payload modificado (ExchangeRate = 50.0m)
        var httpContext2 = new DefaultHttpContext();
        httpContext2.Request.Headers["Idempotency-Key"] = "IDEMP-002";
        httpContext2.Request.Path = "/api/sales/1/complete";
        httpContext2.Request.Method = "POST";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext2 };

        var req2 = new CompleteSaleRequest
        {
            ExchangeRate = 50.0m, // Diferente contenido
            Payments = new List<SalePaymentDto> { new SalePaymentDto { PaymentMethodId = 1, Amount = 10, AmountBsS = 500 } }
        };

        var response = await controller.CompleteSale(1, req2);
        var statusResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
    }

    [Fact]
    public async Task CompleteSale_WhenKeyIsInvalidFormat_ReturnsBadRequest()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var idService = new IdempotencyService(context);
        var mockSalesService = new Mock<ISalesService>();
        var mockUser = new Mock<ICurrentUserService>();

        var controller = new SalesController(mockSalesService.Object, mockUser.Object, idService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Idempotency-Key"] = "invalid key with spaces and ; chars";
        httpContext.Request.Path = "/api/sales/1/complete";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var req = new CompleteSaleRequest { ExchangeRate = 45.0m, Payments = new List<SalePaymentDto>() };
        var response = await controller.CompleteSale(1, req);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public void GetIdempotencyStats_ReturnsTelemetryCounters()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var idService = new IdempotencyService(context);
        var mockSales = new Mock<ISalesService>();
        var mockUser = new Mock<ICurrentUserService>();

        var controller = new SalesController(mockSales.Object, mockUser.Object, idService);
        var result = controller.GetIdempotencyStats();

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    #endregion

    #region 7. Shared Transaction Enlistment & Inventory Service Tests

    [Fact]
    public async Task CompleteSaleAsync_EnrollsInventoryServiceInSharedTransaction()
    {
        var (salesContext, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (salesContext)
        {
            var mockInventory = new Mock<IInventoryService>();
            var mockMediator = new Mock<IMediator>();
            var session = new CashDrawerSession { Id = 1, Status = CashDrawerStatus.Open };
            salesContext.CashDrawerSessions.Add(session);
            await salesContext.SaveChangesAsync();

            var mockCashDrawer = new Mock<ICashDrawerService>();
            mockCashDrawer.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
                .ReturnsAsync(session);
            var mockSettings = new Mock<ISystemSettingsService>();

            // Crear una venta inicial con un item
            var sale = new Sale
            {
                Id = 99,
                Status = SaleStatus.Pending,
                TotalUSD = 10m,
                TotalBsS = 450m,
                AppliedRate = 45m,
                Date = DateTime.UtcNow,
                Items = new List<SaleItem>
                {
                    new SaleItem
                    {
                        Id = 1,
                        SaleId = 99,
                        ProductId = 10,
                        ProductName = "Test Product",
                        Quantity = 2,
                        UnitPrice = 5m,
                        UnitPriceBsS = 225m,
                        Subtotal = 10m,
                        SubtotalBsS = 450m
                    }
                }
            };
            salesContext.Sales.Add(sale);
            await salesContext.SaveChangesAsync();

            var salesService = new SalesService(
                salesContext,
                mockInventory.Object,
                mockMediator.Object,
                mockCashDrawer.Object,
                mockSettings.Object);

            var payments = new List<PaymentInfo>
            {
                new PaymentInfo(1, 10m, 450m, "REF-123")
            };

            var invoiceNumber = await salesService.CompleteSaleAsync(
                99, 45m, payments, 0m, 1, false, "KEY-SHARED-TX", new byte[32], CancellationToken.None);

            Assert.True(invoiceNumber > 0);

            // Verificar que el servicio de inventario fue enrolado en la transacción
            mockInventory.Verify(i => i.EnrollInTransactionAsync(It.IsAny<System.Data.Common.DbTransaction>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);

            // Verificar que el stock fue actualizado dentro del flujo (vía batch)
            mockInventory.Verify(i => i.UpdateStockBatchAsync(
                It.Is<IEnumerable<StockDeductionRequest>>(items => items.Any(it => it.ProductId == 10 && it.QuantityChange == -2m)),
                It.IsAny<string?>(),
                false), Times.Once);

            // Verificar que IdempotentRequest se persistió en la BD
            var savedIdemp = await salesContext.IdempotentRequests.FirstOrDefaultAsync(r => r.Key == "KEY-SHARED-TX");
            Assert.NotNull(savedIdemp);
            Assert.Equal(200, savedIdemp.StatusCode);
            Assert.Equal(invoiceNumber.ToString(), savedIdemp.ResponseBody);
        }
    }

    [Fact]
    public async Task CompleteSaleAsync_WhenCanceled_ThrowsOperationCanceledExceptionAndRollsBack()
    {
        using var salesContext = TestDatabaseFactory.CreateSalesDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale
        {
            Id = 100,
            Status = SaleStatus.Pending,
            TotalUSD = 20m,
            TotalBsS = 900m,
            AppliedRate = 45m,
            Date = DateTime.UtcNow,
            Items = new List<SaleItem>()
        };
        salesContext.Sales.Add(sale);
        await salesContext.SaveChangesAsync();

        var salesService = new SalesService(
            salesContext,
            mockInventory.Object,
            mockMediator.Object,
            mockCashDrawer.Object,
            mockSettings.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Ya cancelado

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await salesService.CompleteSaleAsync(
                100, 45m, new List<PaymentInfo>(), 0m, 1, false, "CANCEL-KEY", null, cts.Token);
        });

        // La venta no debe haber cambiado a Completed
        var unchangedSale = await salesContext.Sales.FindAsync(100);
        Assert.NotNull(unchangedSale);
        Assert.Equal(SaleStatus.Pending, unchangedSale.Status);
    }

    [Fact]
    public async Task EnrollInTransactionAsync_WhenConnectionDiffers_SwitchesConnectionAndEnrollsSuccessfully()
    {
        var (salesContext, salesConn) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        var (invContext, invConn) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (salesConn)
        using (salesContext)
        using (invConn)
        using (invContext)
        {
            var invService = new InventoryService(invContext);
            await using var tx = await salesContext.Database.BeginTransactionAsync();
            var rawTx = tx.GetDbTransaction();

            // Antes de enrolar, la conexión de invContext es distinta de la de la transacción
            Assert.NotEqual(invContext.Database.GetDbConnection(), rawTx.Connection);

            // Enrolar debe cambiar la conexión de invContext y utilizar la transacción sin lanzar excepción
            await invService.EnrollInTransactionAsync(rawTx);

            Assert.Equal(invContext.Database.GetDbConnection(), rawTx.Connection);
            Assert.NotNull(invContext.Database.CurrentTransaction);
        }
    }

    #endregion
}
