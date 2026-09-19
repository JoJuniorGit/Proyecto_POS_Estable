using System;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Integration;

public class ForwardedHeadersIntegrationTests
{
    private readonly Mock<INetworkDiscoveryService> _networkDiscoveryMock;

    public ForwardedHeadersIntegrationTests()
    {
        _networkDiscoveryMock = new Mock<INetworkDiscoveryService>();
        _networkDiscoveryMock.Setup(s => s.GetPairingInfo(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(new ServerPairingInfo
            {
                ServerName = "POS-TEST",
                PrimaryIpAddress = "192.168.1.10",
                HttpPort = 5000,
                HttpsPort = 5001,
                PrimaryHttpUrl = "http://192.168.1.10:5000",
                PrimaryHttpsUrl = "https://192.168.1.10:5001",
                QrPayload = "https://192.168.1.10:5001?paired=true"
            });
    }

    [Fact]
    public async Task ForwardedHeaders_LoopbackProxy_SetsRemoteIpFromXForwardedFor()
    {
        // Arrange
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        // Loopback is trusted by default in ForwardedHeadersOptions
        var middleware = new ForwardedHeadersMiddleware(
            next: (ctx) => Task.CompletedTask,
            loggerFactory: NullLoggerFactory.Instance,
            options: Options.Create(options)
        );

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-For"] = "192.168.1.75";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        // Act
        await middleware.Invoke(context);

        // Assert
        Assert.Equal(IPAddress.Parse("192.168.1.75"), context.Connection.RemoteIpAddress);
        Assert.Equal("https", context.Request.Scheme);
    }

    [Fact]
    public async Task ForwardedHeaders_ExternalProxyConfiguredViaKnownProxies_SetsRemoteIp()
    {
        // Arrange: Reverse proxy on remote server 10.0.0.5 configured via KnownProxies
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        options.KnownProxies.Add(IPAddress.Parse("10.0.0.5"));

        var middleware = new ForwardedHeadersMiddleware(
            next: (ctx) => Task.CompletedTask,
            loggerFactory: NullLoggerFactory.Instance,
            options: Options.Create(options)
        );

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.195";

        // Act
        await middleware.Invoke(context);

        // Assert
        Assert.Equal(IPAddress.Parse("203.0.113.195"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task ForwardedHeaders_UntrustedProxy_DoesNotSetRemoteIp()
    {
        // Arrange: Proxy not in KnownNetworks or KnownProxies
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };

        var middleware = new ForwardedHeadersMiddleware(
            next: (ctx) => Task.CompletedTask,
            loggerFactory: NullLoggerFactory.Instance,
            options: Options.Create(options)
        );

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1"); // Untrusted external IP
        context.Request.Headers["X-Forwarded-For"] = "192.168.1.75";

        // Act
        await middleware.Invoke(context);

        // Assert: RemoteIpAddress remains the untrusted proxy IP, spoofed header rejected
        Assert.Equal(IPAddress.Parse("198.51.100.1"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public void PairingController_WhenLocal_AllowsAnonymous()
    {
        // Arrange
        var controller = new PairingController(_networkDiscoveryMock.Object, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new HttpsRuntimeInfo(Enabled: false, HttpPort: 5000, HttpsPort: 5001));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        // Act
        var result = controller.GetPairingInfo();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public void PairingController_WhenRemoteAndUnauthenticated_Returns403Forbidden()
    {
        // Arrange
        var controller = new PairingController(_networkDiscoveryMock.Object, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new HttpsRuntimeInfo(Enabled: false, HttpPort: 5000, HttpsPort: 5001));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.88");
        // No user authenticated
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        // Act
        var result = controller.GetPairingInfo();

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public void PairingController_WhenRemoteAndAuthenticated_Returns200OK()
    {
// Arrange
        var controller = new PairingController(_networkDiscoveryMock.Object, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new HttpsRuntimeInfo(Enabled: false, HttpPort: 5000, HttpsPort: 5001));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.88");
        // 8.7-L1: el acceso no-local exige rol Admin/Manager, no basta con estar autenticado.
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "AdminPrincipal"),
            new Claim(ClaimTypes.Role, "Admin")
        }, "TestAuth");
        context.User = new ClaimsPrincipal(identity);
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        // Act
        var result = controller.GetPairingInfo();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task PairingController_BehindLoopbackProxy_WithExternalClient_CorrectlyReturns403Forbidden()
    {
        // Arrange: Reverse proxy on loopback forwarding an unauthenticated external client
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        var middleware = new ForwardedHeadersMiddleware(
            next: (ctx) => Task.CompletedTask,
            loggerFactory: NullLoggerFactory.Instance,
            options: Options.Create(options)
        );

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-For"] = "192.168.1.100";

        // Act 1: Run through ForwardedHeaders middleware
        await middleware.Invoke(context);

        // Act 2: Execute PairingController
        var controller = new PairingController(_networkDiscoveryMock.Object, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new HttpsRuntimeInfo(Enabled: false, HttpPort: 5000, HttpsPort: 5001));
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var result = controller.GetPairingInfo();

        // Assert: The proxy header is processed, remote IP is 192.168.1.100, anonymous access blocked
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public void PairingController_WhenHttpsRuntimeEnabled_AdvertisesHttpsPortAndSecurePayload()
    {
        // Arrange
        _networkDiscoveryMock
            .Setup(s => s.GetPairingInfo(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((int httpPort, int httpsPort, bool isHttpsEnabled) => new ServerPairingInfo
            {
                HttpPort = httpPort,
                HttpsPort = httpsPort,
                IsHttpsEnabled = isHttpsEnabled,
                QrPayload = isHttpsEnabled
                    ? $"https://192.168.1.10:{httpsPort}/?paired=true&pair=TESTTOKEN"
                    : $"http://192.168.1.10:{httpPort}/?paired=true&pair=TESTTOKEN"
            });

        var controller = new PairingController(
            _networkDiscoveryMock.Object,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new HttpsRuntimeInfo(Enabled: true, HttpPort: 5000, HttpsPort: 5001));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("192.168.1.10", 5000);
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        // Act
        var result = controller.GetPairingInfo();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var info = Assert.IsType<ServerPairingInfo>(okResult.Value);
        Assert.True(info.IsHttpsEnabled);
        Assert.Equal(5001, info.HttpsPort);
        Assert.StartsWith("https://", info.QrPayload);
    }

    [Fact]
    public void PairingController_WhenHttpsRuntimeDisabled_AdvertisesHttpPortAndInsecurePayload()
    {
        // Arrange
        _networkDiscoveryMock
            .Setup(s => s.GetPairingInfo(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((int httpPort, int httpsPort, bool isHttpsEnabled) => new ServerPairingInfo
            {
                HttpPort = httpPort,
                HttpsPort = httpsPort,
                IsHttpsEnabled = isHttpsEnabled,
                QrPayload = isHttpsEnabled
                    ? $"https://192.168.1.10:{httpsPort}/?paired=true&pair=TESTTOKEN"
                    : $"http://192.168.1.10:{httpPort}/?paired=true&pair=TESTTOKEN"
            });

        var controller = new PairingController(
            _networkDiscoveryMock.Object,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new HttpsRuntimeInfo(Enabled: false, HttpPort: 5000, HttpsPort: 5001));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("192.168.1.10", 5000);
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        // Act
        var result = controller.GetPairingInfo();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var info = Assert.IsType<ServerPairingInfo>(okResult.Value);
        Assert.False(info.IsHttpsEnabled);
        Assert.StartsWith("http://", info.QrPayload);
        Assert.Contains(":5000", info.QrPayload);
    }
}
