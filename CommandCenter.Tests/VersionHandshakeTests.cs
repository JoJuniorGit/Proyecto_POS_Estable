using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.API.Middleware;
using Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommandCenter.Tests;

public class MockOptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
{
    public MockOptionsMonitor(T currentValue)
    {
        CurrentValue = currentValue;
    }

    public T CurrentValue { get; set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener) => null!;
}

public class VersionHandshakeTests
{
    [Fact]
    public async Task VersionCheckMiddleware_RejectsObsoleteClient_WithHttp426()
    {
        var settings = new SystemSettingsOptions
        {
            MinimumClientVersion = "1.2.0",
            ServerVersion = "1.2.0"
        };
        var optionsMonitor = new MockOptionsMonitor<SystemSettingsOptions>(settings);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/sales";
        context.Request.Method = "GET";
        context.Request.Headers["X-Client-Version"] = "1.0.0"; // Obsolete!
        context.Response.Body = new MemoryStream();

        bool nextCalled = false;
        var middleware = new VersionCheckMiddleware(innerContext =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, optionsMonitor);

        Assert.False(nextCalled);
        Assert.Equal((int)HttpStatusCode.UpgradeRequired, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("ClientVersionObsolete", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("1.2.0", doc.RootElement.GetProperty("minimumClientVersion").GetString());
    }

    [Fact]
    public async Task VersionCheckMiddleware_AllowsCompatibleClient()
    {
        var settings = new SystemSettingsOptions
        {
            MinimumClientVersion = "1.0.0",
            ServerVersion = "1.0.0"
        };
        var optionsMonitor = new MockOptionsMonitor<SystemSettingsOptions>(settings);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/sales";
        context.Request.Method = "GET";
        context.Request.Headers["X-Client-Version"] = "1.0.0";
        context.Response.Body = new MemoryStream();

        bool nextCalled = false;
        var middleware = new VersionCheckMiddleware(innerContext =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, optionsMonitor);

        Assert.True(nextCalled);
    }
}
