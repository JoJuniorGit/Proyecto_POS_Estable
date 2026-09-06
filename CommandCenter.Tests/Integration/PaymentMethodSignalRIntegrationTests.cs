using Backend.API.Hubs;
using Backend.API.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests.Integration;

[Trait("Category", "RequiresDocker")]
public class PaymentMethodSignalRIntegrationTests
{
    private SalesDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    [Fact]
    public async Task SignalRPaymentMethodNotifier_Broadcasts_OnPaymentMethodsUpdated()
    {
        // Arrange
        var mockHubContext = new Mock<IHubContext<ExchangeRateHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        var mockLogger = new Mock<ILogger<SignalRPaymentMethodNotifier>>();

        mockHubContext.SetupGet(h => h.Clients).Returns(mockClients.Object);
        mockClients.SetupGet(c => c.All).Returns(mockClientProxy.Object);

        mockClientProxy.Setup(cp => cp.SendCoreAsync(
            "OnPaymentMethodsUpdated",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var notifier = new SignalRPaymentMethodNotifier(mockHubContext.Object);

        // Act
        await notifier.NotifyPaymentMethodsUpdatedAsync();

        // Assert
        mockClientProxy.Verify(cp => cp.SendCoreAsync(
            "OnPaymentMethodsUpdated",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PaymentMethodService_WithSignalRNotifier_EmitsBroadcastOnCreateAndUpdateAndDelete()
    {
        // Arrange
        using var context = CreateInMemoryDbContext();

        var mockHubContext = new Mock<IHubContext<ExchangeRateHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();

        mockHubContext.SetupGet(h => h.Clients).Returns(mockClients.Object);
        mockClients.SetupGet(c => c.All).Returns(mockClientProxy.Object);
        mockClientProxy.Setup(cp => cp.SendCoreAsync(
            "OnPaymentMethodsUpdated",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var notifier = new SignalRPaymentMethodNotifier(mockHubContext.Object);
        var service = new PaymentMethodService(context, notifier: notifier);

        // Act 1: Create
        var created = await service.CreateAsync(new PaymentMethod { Name = "Paypal Digital", IsCash = false, IsActive = true });
        
        // Act 2: Update
        created.RequiresReference = true;
        await service.UpdateAsync(created);

        // Act 3: Delete
        await service.DeleteAsync(created.Id);

        // Assert: Broadcast emitted 3 times (create, update, delete)
        mockClientProxy.Verify(cp => cp.SendCoreAsync(
            "OnPaymentMethodsUpdated",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
