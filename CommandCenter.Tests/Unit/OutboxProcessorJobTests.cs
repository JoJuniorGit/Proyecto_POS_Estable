using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Jobs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class OutboxProcessorJobTests
{
    private SalesDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new SalesDbContext(options);
    }

    [Fact]
    public void OutboxProcessorJob_IsRegisteredAsHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedService<OutboxProcessorJob>();

        var hostedServices = services.Where(s => s.ServiceType == typeof(IHostedService)).ToList();
        var hasOutboxJob = hostedServices.Any(s => s.ImplementationType == typeof(OutboxProcessorJob));

        Assert.True(hasOutboxJob, "OutboxProcessorJob must be registered as IHostedService.");
    }

    [Fact]
    public async Task OutboxProcessorJob_ProcessesPendingMessage_MarksAsProcessed()
    {
        string dbName = Guid.NewGuid().ToString();
        using var dbContext = CreateInMemoryDbContext(dbName);

        var messageId = Guid.NewGuid();
        var outboxMessage = new OutboxMessage
        {
            Id = messageId,
            EventType = "SaleCompleted",
            Payload = JsonSerializer.Serialize(new { SaleId = 10, InvoiceNumber = 100 }),
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            NextRetryUtc = DateTime.UtcNow.AddSeconds(-10),
            Status = "Pending",
            RetryCount = 0
        };
        dbContext.OutboxMessages.Add(outboxMessage);
        await dbContext.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => CreateInMemoryDbContext(dbName));

        var serviceProvider = services.BuildServiceProvider();
        var logger = new Mock<ILogger<OutboxProcessorJob>>();
        var job = new OutboxProcessorJob(serviceProvider.GetRequiredService<IServiceScopeFactory>(), logger.Object);

        await job.ProcessPendingMessagesAsync(CancellationToken.None);

        using var verifyContext = CreateInMemoryDbContext(dbName);
        var updated = await verifyContext.OutboxMessages.FindAsync(messageId);
        Assert.NotNull(updated);
        Assert.Equal("Processed", updated.Status);
        Assert.NotNull(updated.ProcessedAtUtc);
        Assert.Null(updated.ErrorMessage);
    }

    [Fact]
    public async Task OutboxProcessorJob_WhenMessageHasInvalidPayload_IncrementsRetryAndAppliesBackoff()
    {
        string dbName = Guid.NewGuid().ToString();
        using var dbContext = CreateInMemoryDbContext(dbName);

        var messageId = Guid.NewGuid();
        // Invalid JSON to trigger deserialization exception in dispatch
        var outboxMessage = new OutboxMessage
        {
            Id = messageId,
            EventType = "SaleCompleted",
            Payload = "INVALID_CORRUPTED_JSON",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            NextRetryUtc = DateTime.UtcNow.AddSeconds(-5),
            Status = "Pending",
            RetryCount = 1
        };
        dbContext.OutboxMessages.Add(outboxMessage);
        await dbContext.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => CreateInMemoryDbContext(dbName));

        var serviceProvider = services.BuildServiceProvider();
        var logger = new Mock<ILogger<OutboxProcessorJob>>();
        var job = new OutboxProcessorJob(serviceProvider.GetRequiredService<IServiceScopeFactory>(), logger.Object);

        await job.ProcessPendingMessagesAsync(CancellationToken.None);

        using var verifyContext = CreateInMemoryDbContext(dbName);
        var updated = await verifyContext.OutboxMessages.FindAsync(messageId);
        Assert.NotNull(updated);
        Assert.Equal("Pending", updated.Status);
        Assert.Equal(2, updated.RetryCount);
        Assert.NotNull(updated.ErrorMessage);
        // Exponential backoff for attempt 2: 2^2 = 4 seconds
        Assert.True(updated.NextRetryUtc > DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public async Task OutboxProcessorJob_WhenRetryReachesFive_MovesToDeadLetter()
    {
        string dbName = Guid.NewGuid().ToString();
        using var dbContext = CreateInMemoryDbContext(dbName);

        var messageId = Guid.NewGuid();
        var outboxMessage = new OutboxMessage
        {
            Id = messageId,
            EventType = "SaleCompleted",
            Payload = "CORRUPTED_PAYLOAD_TRIGGERING_FAILURE",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            NextRetryUtc = DateTime.UtcNow.AddSeconds(-5),
            Status = "Pending",
            RetryCount = 4 // 4 previous retries, this run makes it 5
        };
        dbContext.OutboxMessages.Add(outboxMessage);
        await dbContext.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => CreateInMemoryDbContext(dbName));

        var serviceProvider = services.BuildServiceProvider();
        var logger = new Mock<ILogger<OutboxProcessorJob>>();
        var job = new OutboxProcessorJob(serviceProvider.GetRequiredService<IServiceScopeFactory>(), logger.Object);

        await job.ProcessPendingMessagesAsync(CancellationToken.None);

        using var verifyContext = CreateInMemoryDbContext(dbName);
        var updated = await verifyContext.OutboxMessages.FindAsync(messageId);
        Assert.NotNull(updated);
        Assert.Equal("DeadLetter", updated.Status);
        Assert.Equal(5, updated.RetryCount);
        Assert.Contains("Falló tras 5 intentos", updated.ErrorMessage);
    }

    [Fact]
    public async Task CompleteSaleAsync_WithIdempotencyKey_ReturnsExistingInvoice_WhenAlreadyCompleted()
    {
        string dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryDbContext(dbName);

        var pm = new PaymentMethod { Id = 1, Name = "Cash USD", IsActive = true, IsCash = true };
        context.PaymentMethods.Add(pm);

        var sale = new Sale
        {
            Id = 55,
            Status = SaleStatus.Completed,
            InvoiceNumber = 777,
            Date = DateTime.UtcNow,
            TotalUSD = 20m,
            TotalBsS = 1000m,
            AppliedRate = 50m
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var mockInv = new Mock<IInventoryService>();
        var mockMed = new Mock<IMediator>();
        var mockDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var service = new SalesService(context, mockInv.Object, mockMed.Object, mockDrawer.Object, mockSettings.Object);

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 20m, 1000m, null)
        };

        // Act: call CompleteSaleAsync with idempotency key on already completed sale
        int invoiceNumber = await service.CompleteSaleAsync(
            55,
            50m,
            payments,
            roundingAdjustment: 0m,
            cashierId: 1,
            isPendingPickup: false,
            idempotencyKey: "unique-key-xyz-123");

        // Assert: returns existing invoice number 777 safely without throwing
        Assert.Equal(777, invoiceNumber);

        // Inventory deduction must not happen again
        mockInv.Verify(i => i.UpdateStockAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task PurgeProcessedMessagesAsync_PurgesOldProcessedMessages_AndPreservesPendingFailedAndDeadLetter()
    {
        string dbName = Guid.NewGuid().ToString();
        using var dbContext = CreateInMemoryDbContext(dbName);

        var oldProcessedId = Guid.NewGuid();
        var recentProcessedId = Guid.NewGuid();
        var oldPendingId = Guid.NewGuid();
        var oldFailedId = Guid.NewGuid();
        var oldDeadLetterId = Guid.NewGuid();

        // 1. Mensaje procesado hace 10 días (debe ser purgado)
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = oldProcessedId,
            EventType = "SaleCompleted",
            Payload = "{}",
            Status = "Processed",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-11),
            ProcessedAtUtc = DateTime.UtcNow.AddDays(-10),
            NextRetryUtc = DateTime.UtcNow.AddDays(-11)
        });

        // 2. Mensaje procesado hace 2 días (debe conservarse por retención de 7 días)
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = recentProcessedId,
            EventType = "SaleCompleted",
            Payload = "{}",
            Status = "Processed",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-3),
            ProcessedAtUtc = DateTime.UtcNow.AddDays(-2),
            NextRetryUtc = DateTime.UtcNow.AddDays(-3)
        });

        // 3. Mensaje pendiente antiguo (NUNCA debe ser purgado)
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = oldPendingId,
            EventType = "SaleCompleted",
            Payload = "{}",
            Status = "Pending",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-10),
            NextRetryUtc = DateTime.UtcNow.AddDays(-10)
        });

        // 4. Mensaje fallido antiguo (NUNCA debe ser purgado)
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = oldFailedId,
            EventType = "SaleCompleted",
            Payload = "{}",
            Status = "Failed",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-10),
            ProcessedAtUtc = DateTime.UtcNow.AddDays(-10),
            NextRetryUtc = DateTime.UtcNow.AddDays(-10),
            ErrorMessage = "Error simulado"
        });

        // 5. Mensaje DeadLetter antiguo (NUNCA debe ser purgado)
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = oldDeadLetterId,
            EventType = "SaleCompleted",
            Payload = "{}",
            Status = "DeadLetter",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-10),
            ProcessedAtUtc = DateTime.UtcNow.AddDays(-10),
            NextRetryUtc = DateTime.UtcNow.AddDays(-10),
            ErrorMessage = "Dead letter"
        });

        await dbContext.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateInMemoryDbContext(dbName));
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        var job = new OutboxProcessorJob(sp.GetRequiredService<IServiceScopeFactory>(), Mock.Of<ILogger<OutboxProcessorJob>>());

        // Act: Purgar mensajes procesados con más de 7 días de antigüedad
        int purged = await job.PurgeProcessedMessagesAsync(DateTime.UtcNow.AddDays(-7));

        // Assert: Solo 1 registro debe haber sido eliminado
        Assert.Equal(1, purged);

        using var verifyContext = CreateInMemoryDbContext(dbName);
        var remainingMessages = await verifyContext.OutboxMessages.ToListAsync();

        Assert.Equal(4, remainingMessages.Count);
        Assert.DoesNotContain(remainingMessages, m => m.Id == oldProcessedId);
        Assert.Contains(remainingMessages, m => m.Id == recentProcessedId);
        Assert.Contains(remainingMessages, m => m.Id == oldPendingId);
        Assert.Contains(remainingMessages, m => m.Id == oldFailedId);
        Assert.Contains(remainingMessages, m => m.Id == oldDeadLetterId);
    }
}

