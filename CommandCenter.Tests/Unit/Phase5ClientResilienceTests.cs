using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.API.Middleware;
using Backend.API.Services;
using Core.Common;
using Core.Entities;
using Desktop.Client.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase5ClientResilienceTests
{
    private class InMemoryTokenStorage : ISecureTokenStorageService
    {
        private string? _token;
        public InMemoryTokenStorage(string? token = null) => _token = token;
        public void SaveToken(string token) => _token = token;
        public string? LoadToken() => _token;
        public void ClearToken() => _token = null;
    }

    private ITokenService CreateTokenService()
    {
        var inMemorySettings = new System.Collections.Generic.Dictionary<string, string?>
        {
            { "JWT_SETTINGS_KEY", "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!" },
            { "JwtSettings:Issuer", "SolucionesPos" },
            { "JwtSettings:Audience", "PosClient" },
            { "JwtSettings:ExpiryMinutes", "120" }
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        return new TokenService(config);
    }

    [Fact]
    public void UserSession_TryRestoreTokenFromStorage_ValidDesktopToken_RestoresSessionSuccessfully()
    {
        var tokenService = CreateTokenService();
        var user = new User
        {
            Id = 42,
            Cedula = "V-99887766",
            Name = "Cajero Principal",
            Username = "cajero42",
            Role = UserRole.Cashier,
            SecurityStamp = Guid.NewGuid().ToString()
        };

        var token = tokenService.GenerateToken(user, "pos:desktop");
        var storage = new InMemoryTokenStorage(token);
        var session = new UserSession(storage);

        bool result = session.TryRestoreTokenFromStorage();

        Assert.True(result);
        Assert.True(session.IsLoggedIn);
        Assert.Equal(42, session.CurrentUser?.Id);
        Assert.Equal("Cajero Principal", session.CurrentUser?.Name);
        Assert.Equal(UserRole.Cashier, session.CurrentUser?.Role);
        Assert.Equal(token, session.Token);
    }

    [Fact]
    public void UserSession_TryRestoreTokenFromStorage_ExpiredToken_PurgesAndReturnsFalse()
    {
        // Build an expired token manually (exp = 1000, which is in 1970)
        string header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        string payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"sub\":\"10\",\"name\":\"Expired User\",\"role\":\"Cashier\",\"scope\":\"pos:desktop\",\"exp\":1000}")).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        string dummySig = "dummysignature123";
        string expiredToken = $"{header}.{payload}.{dummySig}";

        var storage = new InMemoryTokenStorage(expiredToken);
        var session = new UserSession(storage);

        bool result = session.TryRestoreTokenFromStorage();

        Assert.False(result);
        Assert.False(session.IsLoggedIn);
        Assert.Null(session.CurrentUser);
        Assert.Null(storage.LoadToken()); // Confirms token was purged
    }

    [Fact]
    public void UserSession_TryRestoreTokenFromStorage_IncompatibleWebScope_PurgesAndReturnsFalse()
    {
        var tokenService = CreateTokenService();
        var user = new User
        {
            Id = 15,
            Cedula = "V-11223344",
            Name = "Web Cashier",
            Username = "webcashier",
            Role = UserRole.Cashier,
            SecurityStamp = Guid.NewGuid().ToString()
        };

        // Generates token with scope: pos:web
        var token = tokenService.GenerateToken(user, "pos:web");
        var storage = new InMemoryTokenStorage(token);
        var session = new UserSession(storage);

        bool result = session.TryRestoreTokenFromStorage();

        Assert.False(result);
        Assert.False(session.IsLoggedIn);
        Assert.Null(storage.LoadToken()); // Confirms web token was purged from desktop storage
    }

    [Theory]
    [InlineData(typeof(ArgumentException), 400, "BadRequest")]
    [InlineData(typeof(KeyNotFoundException), 404, "NotFound")]
    [InlineData(typeof(UnauthorizedAccessException), 403, "Forbidden")]
    public async Task GlobalExceptionHandler_MapsDomainExceptions_ToExpectedStatusCodes(Type exceptionType, int expectedStatusCode, string expectedError)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/test";
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            var ex = (Exception)Activator.CreateInstance(exceptionType, "Test domain failure message")!;
            throw ex;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal(expectedError, doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TaskExtensions_SafeFireAndForget_HandlesExceptionsWithoutCrash()
    {
        bool caughtInHandler = false;
        var tcs = new TaskCompletionSource();

        async Task FaultyTask()
        {
            await Task.Yield();
            throw new InvalidOperationException("Test background fault");
        }

        FaultyTask().SafeFireAndForget("UnitTest.Context", ex =>
        {
            caughtInHandler = true;
            tcs.SetResult();
        });

        await Task.WhenAny(tcs.Task, Task.Delay(1000));
        Assert.True(caughtInHandler);
    }
}
