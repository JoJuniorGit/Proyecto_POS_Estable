using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.157 (SEC-07, REQ-HREV-02): filtro global de hubs StampValidationHubFilter. Verifica que el
/// pipeline fluye sin cambios con sello vigente (connect/invoke) y que un sello ausente o
/// revocado aborta la conexión y lanza HubException con los mensajes EXACTOS del
/// SecurityStampValidationMiddleware. Los contextos de SignalR se mockean con HubCallerContext.
/// </summary>
public class StampValidationHubFilterTests
{
    private const string MissingStampMessage = "El token no posee un sello de seguridad válido. Por favor inicie sesión nuevamente.";
    private const string InvalidStampMessage = "La sesión ha sido revocada o las credenciales del usuario cambiaron. Inicie sesión nuevamente.";

    private static Mock<HubCallerContext> CreateCallerContext(int? userId, string? securityStamp)
    {
        var claims = new List<Claim>();
        if (userId is int id)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString(CultureInfo.InvariantCulture)));
        }
        if (securityStamp is not null)
        {
            claims.Add(new Claim("security_stamp", securityStamp));
        }

        var context = new Mock<HubCallerContext>();
        context.SetupGet(caller => caller.ConnectionId).Returns("conn-test");
        context.SetupGet(caller => caller.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")));
        return context;
    }

    private static Mock<HubCallerContext> CreateCallerContextWithoutPrincipal()
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(caller => caller.ConnectionId).Returns("conn-test");
        context.SetupGet(caller => caller.User).Returns((ClaimsPrincipal?)null);
        return context;
    }

    private static HubLifetimeContext CreateLifetimeContext(HubCallerContext caller)
        => new(caller, Mock.Of<IServiceProvider>(), new Mock<Hub>().Object);

    private static HubInvocationContext CreateInvocationContext(HubCallerContext caller)
        => new(
            caller,
            Mock.Of<IServiceProvider>(),
            new Mock<Hub>().Object,
            typeof(AuthorizationHub).GetMethod(nameof(AuthorizationHub.RequestAuthorizationAsync))!,
            Array.Empty<object?>());

    private static Mock<ISecurityStampValidator> CreateValidator(int userId, string stamp, bool isValid)
    {
        var validator = new Mock<ISecurityStampValidator>();
        validator.Setup(v => v.ValidateStampAsync(userId, stamp, true)).ReturnsAsync(isValid);
        return validator;
    }

    [Fact]
    public async Task OnConnectedAsync_WithValidStamp_InvokesNextWithoutAbort()
    {
        var validator = CreateValidator(7, "stamp-vigente", isValid: true);
        var filter = new StampValidationHubFilter(validator.Object);
        var caller = CreateCallerContext(7, "stamp-vigente");
        var nextCalled = false;
        Func<HubLifetimeContext, Task> next = _ => { nextCalled = true; return Task.CompletedTask; };

        await filter.OnConnectedAsync(CreateLifetimeContext(caller.Object), next);

        Assert.True(nextCalled);
        caller.Verify(c => c.Abort(), Times.Never);
        validator.Verify(v => v.ValidateStampAsync(7, "stamp-vigente", true), Times.Once);
    }

    [Fact]
    public async Task InvokeMethodAsync_WithValidStamp_InvokesNextAndReturnsResult()
    {
        var validator = CreateValidator(7, "stamp-vigente", isValid: true);
        var filter = new StampValidationHubFilter(validator.Object);
        var caller = CreateCallerContext(7, "stamp-vigente");
        var nextCalled = false;
        Func<HubInvocationContext, ValueTask<object?>> next =
            _ => { nextCalled = true; return ValueTask.FromResult<object?>("resultado-hub"); };

        var result = await filter.InvokeMethodAsync(CreateInvocationContext(caller.Object), next);

        Assert.True(nextCalled);
        Assert.Equal("resultado-hub", result);
        caller.Verify(c => c.Abort(), Times.Never);
        validator.Verify(v => v.ValidateStampAsync(7, "stamp-vigente", true), Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_WithRevokedStamp_AbortsAndThrowsCanonicalMessage()
    {
        var validator = CreateValidator(7, "stamp-revocado", isValid: false);
        var filter = new StampValidationHubFilter(validator.Object);
        var caller = CreateCallerContext(7, "stamp-revocado");
        var nextCalled = false;
        Func<HubLifetimeContext, Task> next = _ => { nextCalled = true; return Task.CompletedTask; };

        var exception = await Assert.ThrowsAsync<HubException>(
            () => filter.OnConnectedAsync(CreateLifetimeContext(caller.Object), next));

        Assert.Equal(InvalidStampMessage, exception.Message);
        Assert.False(nextCalled);
        caller.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task InvokeMethodAsync_WithRevokedStamp_AbortsAndThrowsCanonicalMessage()
    {
        var validator = CreateValidator(7, "stamp-revocado", isValid: false);
        var filter = new StampValidationHubFilter(validator.Object);
        var caller = CreateCallerContext(7, "stamp-revocado");
        var nextCalled = false;
        Func<HubInvocationContext, ValueTask<object?>> next =
            _ => { nextCalled = true; return ValueTask.FromResult<object?>(null); };

        var exception = await Assert.ThrowsAsync<HubException>(
            () => filter.InvokeMethodAsync(CreateInvocationContext(caller.Object), next).AsTask());

        Assert.Equal(InvalidStampMessage, exception.Message);
        Assert.False(nextCalled);
        caller.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_WithoutStampClaim_AbortsAndThrowsMissingMessage()
    {
        var validator = new Mock<ISecurityStampValidator>();
        var filter = new StampValidationHubFilter(validator.Object);
        var caller = CreateCallerContext(7, securityStamp: null);
        var nextCalled = false;
        Func<HubLifetimeContext, Task> next = _ => { nextCalled = true; return Task.CompletedTask; };

        var exception = await Assert.ThrowsAsync<HubException>(
            () => filter.OnConnectedAsync(CreateLifetimeContext(caller.Object), next));

        Assert.Equal(MissingStampMessage, exception.Message);
        Assert.False(nextCalled);
        caller.Verify(c => c.Abort(), Times.Once);
        validator.Verify(
            v => v.ValidateStampAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task InvokeMethodAsync_WithoutUserIdClaim_AbortsAndThrowsMissingMessage()
    {
        var validator = new Mock<ISecurityStampValidator>();
        var filter = new StampValidationHubFilter(validator.Object);
        var caller = CreateCallerContext(userId: null, securityStamp: "stamp-presente");
        var nextCalled = false;
        Func<HubInvocationContext, ValueTask<object?>> next =
            _ => { nextCalled = true; return ValueTask.FromResult<object?>(null); };

        var exception = await Assert.ThrowsAsync<HubException>(
            () => filter.InvokeMethodAsync(CreateInvocationContext(caller.Object), next).AsTask());

        Assert.Equal(MissingStampMessage, exception.Message);
        Assert.False(nextCalled);
        caller.Verify(c => c.Abort(), Times.Once);
        validator.Verify(
            v => v.ValidateStampAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_WithoutPrincipal_AbortsAndThrowsMissingMessage()
    {
        var filter = new StampValidationHubFilter(new Mock<ISecurityStampValidator>().Object);
        var caller = CreateCallerContextWithoutPrincipal();
        var nextCalled = false;
        Func<HubLifetimeContext, Task> next = _ => { nextCalled = true; return Task.CompletedTask; };

        var exception = await Assert.ThrowsAsync<HubException>(
            () => filter.OnConnectedAsync(CreateLifetimeContext(caller.Object), next));

        Assert.Equal(MissingStampMessage, exception.Message);
        Assert.False(nextCalled);
        caller.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_DelegatesToNextWithException()
    {
        var filter = new StampValidationHubFilter(new Mock<ISecurityStampValidator>().Object);
        var caller = CreateCallerContext(7, "stamp-vigente");
        var expected = new InvalidOperationException("conexion cerrada");
        Exception? captured = null;
        var nextCalled = false;

        await filter.OnDisconnectedAsync(
            CreateLifetimeContext(caller.Object),
            expected,
            (_, exception) => { nextCalled = true; captured = exception; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.Same(expected, captured);
        caller.Verify(c => c.Abort(), Times.Never);
    }
}
