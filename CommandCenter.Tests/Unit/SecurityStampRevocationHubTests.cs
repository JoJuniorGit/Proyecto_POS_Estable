using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Services;
using Core.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.157 (SEC-07, REQ-HREV-01): push de desconexión del SecurityStampValidator. Los IHubContext
/// se mockean (IHubContext -> IHubClients -> IClientProxy) para verificar el grupo user:{id} y el
/// evento exacto "ForceDisconnect" sin payload en ambos hubs, el fail-soft del transporte y el
/// no-op cuando el validador se construye sin hubs (tests unitarios del validador).
/// </summary>
public class SecurityStampRevocationHubTests
{
    private const int UserId = 42;
    private const string InitialStamp = "stamp-inicial-8157";
    private const string ForceDisconnectEvent = "ForceDisconnect";

    private static async Task<SalesDbContext> CreateDbWithUserAsync()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new SalesDbContext(options);
        db.Users.Add(new User
        {
            Id = UserId,
            Cedula = "V-00000042",
            Username = "usuario42",
            Name = "Usuario 42",
            FullName = "Usuario 42",
            Role = UserRole.Cashier,
            IsActive = true,
            SecurityStamp = InitialStamp
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public void UserGroup_BothHubs_UseCanonicalUserFormat()
    {
        Assert.Equal("user:42", AuthorizationHub.UserGroup(42));
        Assert.Equal(AuthorizationHub.UserGroup(42), ExchangeRateHub.UserGroup(42));
    }

    [Fact]
    public async Task RevokeUserStampAsync_PushesForceDisconnectToBothHubsAndRotatesStamp()
    {
        using var db = await CreateDbWithUserAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var authorization = new HubMock<AuthorizationHub>(UserId);
        var exchangeRate = new HubMock<ExchangeRateHub>(UserId);
        var validator = new SecurityStampValidator(db, cache, authorization.Context.Object, exchangeRate.Context.Object);

        await validator.RevokeUserStampAsync(UserId);

        authorization.VerifyForceDisconnect(UserId);
        exchangeRate.VerifyForceDisconnect(UserId);

        var rotated = await db.Users.AsNoTracking().SingleAsync(u => u.Id == UserId);
        Assert.NotEqual(InitialStamp, rotated.SecurityStamp);
        Assert.False(await validator.ValidateStampAsync(UserId, InitialStamp, forceImmediateCheck: true));
        Assert.True(await validator.ValidateStampAsync(UserId, rotated.SecurityStamp, forceImmediateCheck: true));
    }

    [Fact]
    public async Task RevokeUserStampAsync_WhenOneHubPushFails_IsFailSoftAndRevocationCompletes()
    {
        using var db = await CreateDbWithUserAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var authorization = new HubMock<AuthorizationHub>(UserId);
        authorization.Proxy
            .Setup(proxy => proxy.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub caido"));
        var exchangeRate = new HubMock<ExchangeRateHub>(UserId);
        var validator = new SecurityStampValidator(db, cache, authorization.Context.Object, exchangeRate.Context.Object);

        var exception = await Record.ExceptionAsync(() => validator.RevokeUserStampAsync(UserId));

        Assert.Null(exception);
        // El fallo de un hub no debe impedir el push al otro hub.
        exchangeRate.VerifyForceDisconnect(UserId);

        var rotated = await db.Users.AsNoTracking().SingleAsync(u => u.Id == UserId);
        Assert.NotEqual(InitialStamp, rotated.SecurityStamp);
        Assert.False(await validator.ValidateStampAsync(UserId, InitialStamp, forceImmediateCheck: true));
        Assert.True(await validator.ValidateStampAsync(UserId, rotated.SecurityStamp, forceImmediateCheck: true));
    }

    [Fact]
    public async Task RevokeUserStampAsync_WithoutHubContexts_DoesNotThrowAndCompletesRevocation()
    {
        using var db = await CreateDbWithUserAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var validator = new SecurityStampValidator(db, cache);

        var exception = await Record.ExceptionAsync(() => validator.RevokeUserStampAsync(UserId));

        Assert.Null(exception);
        var rotated = await db.Users.AsNoTracking().SingleAsync(u => u.Id == UserId);
        Assert.NotEqual(InitialStamp, rotated.SecurityStamp);
        Assert.False(await validator.ValidateStampAsync(UserId, InitialStamp, forceImmediateCheck: true));
    }

    [Fact]
    public async Task InvalidateUserSessionsAsync_InvalidatesCacheAndPushesToBothHubs()
    {
        using var db = await CreateDbWithUserAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var authorization = new HubMock<AuthorizationHub>(UserId);
        var exchangeRate = new HubMock<ExchangeRateHub>(UserId);
        var validator = new SecurityStampValidator(db, cache, authorization.Context.Object, exchangeRate.Context.Object);

        // Puebla la caché con el sello vigente y luego lo rota en BD sin invalidar caché: un hit
        // cacheado (ventana de 45 s) seguiría devolviendo true; solo la invalidación lo corta.
        Assert.True(await validator.ValidateStampAsync(UserId, InitialStamp));
        var user = await db.Users.SingleAsync(u => u.Id == UserId);
        user.SecurityStamp = "stamp-rotado-8157";
        await db.SaveChangesAsync();

        await validator.InvalidateUserSessionsAsync(UserId);

        authorization.VerifyForceDisconnect(UserId);
        exchangeRate.VerifyForceDisconnect(UserId);
        Assert.False(await validator.ValidateStampAsync(UserId, InitialStamp));
        Assert.True(await validator.ValidateStampAsync(UserId, "stamp-rotado-8157"));
    }

    /// <summary>Mock encadenado IHubContext -> IHubClients -> IClientProxy para un hub dado.</summary>
    private sealed class HubMock<THub> where THub : Hub
    {
        public Mock<IHubContext<THub>> Context { get; } = new();
        public Mock<IHubClients> Clients { get; } = new();
        public Mock<IClientProxy> Proxy { get; } = new();

        public HubMock(int userId)
        {
            Context.SetupGet(hub => hub.Clients).Returns(Clients.Object);
            Clients.Setup(clients => clients.Group($"user:{userId}")).Returns(Proxy.Object);
            Proxy.Setup(proxy => proxy.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        public void VerifyForceDisconnect(int userId)
        {
            Clients.Verify(clients => clients.Group($"user:{userId}"), Times.Once);
            Proxy.Verify(
                proxy => proxy.SendCoreAsync(
                    ForceDisconnectEvent,
                    It.Is<object[]>(arguments => arguments.Length == 0),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }
}
