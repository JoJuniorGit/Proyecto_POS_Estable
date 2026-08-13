using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.API.Middleware;
using Core.Logging;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

namespace CommandCenter.Tests;

public class GlobalExceptionHandlerMiddlewareTests
{
    [Fact]
    public async Task Middleware_CatchesNpgsqlException_ReturnsHttp503WithJson()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/PaymentMethods/active";
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            throw new NpgsqlException("Test PostgreSQL Authentication Error 28P01");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("DatabaseConnectionError", doc.RootElement.GetProperty("error").GetString());
        Assert.True(File.Exists(AppLogger.DbErrorsLogPath));
    }

    [Fact]
    public async Task Middleware_CatchesGeneralException_ReturnsHttp500WithJson()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/test";
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            throw new InvalidOperationException("Test general failure");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.InternalServerError, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("InternalServerError", doc.RootElement.GetProperty("error").GetString());
    }
}
