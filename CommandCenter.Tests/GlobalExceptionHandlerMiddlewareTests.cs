using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Middleware;
using Core.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
            throw new Exception("Test general failure");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.InternalServerError, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("InternalServerError", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Middleware_CatchesConcurrencyException_ReturnsHttp409WithTraceId()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/sales/1/items";
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            throw new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("Row version conflict");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.Conflict, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("ConcurrencyConflict", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("concurrente", doc.RootElement.GetProperty("message").GetString());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Middleware_CatchesInvalidOperationException_ReturnsHttp409WithTraceId()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/sales/1/cancel";
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            throw new InvalidOperationException("No se puede anular un pedido ya completado.");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.Conflict, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("InvalidOperation", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("No se puede anular un pedido ya completado.", doc.RootElement.GetProperty("message").GetString());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Middleware_CatchesKeyNotFoundException_ReturnsHttp404WithTraceIdAndDualMessage()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/products/9999";
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            throw new System.Collections.Generic.KeyNotFoundException("Producto no encontrado en inventario.");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.NotFound, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("NotFound", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("Producto no encontrado en inventario.", doc.RootElement.GetProperty("message").GetString());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Middleware_CatchesArgumentException_ReturnsHttp400WithTraceIdAndDualMessage()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/sales/create";
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            throw new ArgumentException("La cantidad debe ser mayor a 0.");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.BadRequest, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("BadRequest", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("La cantidad debe ser mayor a 0.", doc.RootElement.GetProperty("message").GetString());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Middleware_CatchesUnauthorizedAccessException_ReturnsHttp403WithTraceIdAndDualMessage()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/products/export";
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            throw new UnauthorizedAccessException("El rol Cajero no tiene permisos de exportación.");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.Forbidden, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("Forbidden", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("El rol Cajero no tiene permisos de exportación.", doc.RootElement.GetProperty("message").GetString());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Middleware_CatchesPostgresException_23502_NotNullViolation_ReturnsHttp400WithFriendlyMessage()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/products";
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            throw new PostgresException("null value in column \"Barcode\" violates not-null constraint", "ERROR", "ERROR", "23502", columnName: "Barcode");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.BadRequest, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("NotNullConstraintViolation", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("El campo 'Barcode' es obligatorio y no puede ser nulo.", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("23502", doc.RootElement.GetProperty("sqlState").GetString());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Middleware_CatchesPostgresException_23505_UniqueViolation_ReturnsHttp409WithFriendlyMessage()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/products";
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            throw new PostgresException("duplicate key value violates unique constraint \"IX_Products_Barcode\"", "ERROR", "ERROR", "23505", constraintName: "IX_Products_Barcode");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.Conflict, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("UniqueConstraintViolation", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("El código de barras ya está asignado a otro producto registrado.", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("23505", doc.RootElement.GetProperty("sqlState").GetString());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Middleware_CatchesPostgresException_23503_ForeignKeyViolation_ReturnsHttp400WithFriendlyMessage()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/sales";
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
        {
            throw new PostgresException("foreign key violation", "ERROR", "ERROR", "23503");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.BadRequest, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.Equal("ForeignKeyViolation", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("referencia un registro inexistente", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("23503", doc.RootElement.GetProperty("sqlState").GetString());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Middleware_CatchesOperationCanceled_ReturnsHttp499WithoutAlarming()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/sales/1";
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        var cts = new CancellationTokenSource();
        cts.Cancel();
        context.Features.Set<IHttpRequestLifetimeFeature>(new CanceledLifetimeFeature(cts.Token));

        var middleware = new GlobalExceptionHandlerMiddleware(innerContext =>
            {
                throw new OperationCanceledException(innerContext.RequestAborted);
            });

        await middleware.InvokeAsync(context);

        Assert.Equal(499, context.Response.StatusCode);
    }

    private sealed class CanceledLifetimeFeature : IHttpRequestLifetimeFeature
    {
        private readonly CancellationToken _aborted;

        public CanceledLifetimeFeature(CancellationToken aborted)
        {
            _aborted = aborted;
        }

        public CancellationToken RequestAborted
        {
            get => _aborted;
            set => _ = value;
        }

        public void Abort() { }
    }
}
