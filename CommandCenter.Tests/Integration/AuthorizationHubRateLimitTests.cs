using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Services;
using Core.Entities;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Integration;

public class AuthorizationHubRateLimitTests
{
    [Fact]
    public async Task RequestAuthorization_WhenRateLimited_DoesNotCallCoordinator()
    {
        var coordinator = new Mock<IAuthorizationCoordinator>();
        var hub = CreateHub(coordinator, new RejectingLimiter(), UserRole.Cashier);

        var result = await hub.RequestAuthorizationAsync(new RequestAuthorizationContract
        {
            SaleId = 1,
            ProductId = 1,
            Quantity = 1m
        });

        Assert.False(result.Success);
        Assert.Contains("7", result.Message);
        coordinator.Verify(
            service => service.CreateAsync(
                It.IsAny<RequestAuthorizationContract>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<UserRole>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveAuthorization_WhenRateLimited_DoesNotCallCoordinator()
    {
        var coordinator = new Mock<IAuthorizationCoordinator>();
        var hub = CreateHub(coordinator, new RejectingLimiter(), UserRole.Admin);

        var result = await hub.ResolveAuthorizationAsync(1, approved: true);

        Assert.False(result.Success);
        Assert.Contains("7", result.Message);
        coordinator.Verify(
            service => service.ResolveAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static AuthorizationHub CreateHub(
        Mock<IAuthorizationCoordinator> coordinator,
        IAuthorizationHubRateLimiter limiter,
        UserRole role)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Name, "Test User"),
            new Claim(ClaimTypes.Role, role.ToString())
        }, "TestAuth");

        var context = new Mock<HubCallerContext>();
        context.SetupGet(caller => caller.User).Returns(new ClaimsPrincipal(identity));

        return new AuthorizationHub(coordinator.Object, limiter)
        {
            Context = context.Object
        };
    }

    private sealed class RejectingLimiter : IAuthorizationHubRateLimiter
    {
        public bool TryAcquire(int userId, string operation, out int retryAfterSeconds)
        {
            retryAfterSeconds = 7;
            return false;
        }
    }
}
