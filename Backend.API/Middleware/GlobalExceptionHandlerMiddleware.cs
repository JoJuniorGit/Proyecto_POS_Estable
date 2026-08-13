using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Core.Logging;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Backend.API.Middleware;

public class GlobalExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;

    public GlobalExceptionHandlerMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var requestPath = $"{context.Request.Method} {context.Request.Path}";
        
        // Find if any exception in chain is PostgresException or NpgsqlException
        var postgresEx = FindException<PostgresException>(exception);
        var npgsqlEx = FindException<NpgsqlException>(exception);

        if (postgresEx != null || npgsqlEx != null)
        {
            AppLogger.LogDbError(exception, $"Request: {requestPath}");

            string sqlState = postgresEx?.SqlState ?? string.Empty;
            string message = "Error de conexión con la base de datos PostgreSQL. Verifique que el servicio de base de datos esté activo y que las credenciales de conexión sean correctas.";

            if (sqlState == "28P01") // Password Authentication Failed
            {
                message = "Fallo de autenticación en PostgreSQL (Usuario/Contraseña incorrectos). Verifique appsettings.json.";
            }

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable; // 503

            var dbErrorPayload = new
            {
                error = "DatabaseConnectionError",
                message = message,
                sqlState = string.IsNullOrEmpty(sqlState) ? null : sqlState
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(dbErrorPayload));
            return;
        }

        // Non-database unhandled exception
        AppLogger.LogCrash(exception, $"Unhandled Exception in Request: {requestPath}");

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError; // 500

        var errorPayload = new
        {
            error = "InternalServerError",
            message = "Ocurrió un error interno al procesar la solicitud."
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(errorPayload));
    }

    private static T? FindException<T>(Exception ex) where T : Exception
    {
        var current = ex;
        while (current != null)
        {
            if (current is T match) return match;
            current = current.InnerException!;
        }
        return null;
    }
}
