using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.212-WU6: the post-commit drawer notification uses the exchange-rate hub and carries the
/// session id verbatim. Controllers invoke it only after the drawer service returns.
/// </summary>
public class SignalRCashDrawerNotifierTests
{
    [Fact]
    public async Task NotifyCashDrawerChangedAsync_SendsTheSessionIdThroughTheHub()
    {
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(proxy => proxy.SendCoreAsync(
                "CashDrawerChanged",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.SetupGet(candidate => candidate.All).Returns(clientProxy.Object);

        var hub = new Mock<IHubContext<ExchangeRateHub>>();
        hub.SetupGet(candidate => candidate.Clients).Returns(clients.Object);

        var notifier = new SignalRCashDrawerNotifier(hub.Object);

        await notifier.NotifyCashDrawerChangedAsync(42);

        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "CashDrawerChanged",
                It.Is<object?[]>(args => args.Length == 1 && (int)args[0]! == 42),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
