using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Core.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        context.Response.ContentType = "application/json";

        // 1. PostgreSQL specific exceptions (H-API-6 / H-API-20)
        var postgresEx = FindException<PostgresException>(exception);
        if (postgresEx != null)
        {
            AppLogger.LogDbError(exception, $"Request: {requestPath}");
            var sqlState = postgresEx.SqlState;

            if (sqlState == "23505") // unique_violation
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                string friendlyMessage = postgresEx.ConstraintName switch
                {
                    "IX_PaymentMethods_Name_Unique_NotDeleted" => "Ya existe un método de pago activo con el mismo nombre.",
                    "IX_Users_Username" => "Ya existe un usuario registrado con ese nombre de usuario.",
                    "IX_Users_Cedula" => "Ya existe un usuario registrado con esa cédula.",
                    "IX_Products_Barcode" => "El código de barras ya está asignado a otro producto registrado.",
                    "IX_Products_SKU" => "El código SKU ya está registrado en el inventario.",
                    "IX_Customers_CedulaOrRif" => "Ya existe un cliente registrado con esta cédula o RIF.",
                    _ => "El registro ya existe o infringe una restricción de unicidad en el sistema."
                };

                var conflictPayload = new
                {
                    type = "https://tools.ietf.org/html/rfc7231#section-6.5.8",
                    title = "Conflicto de Unicidad",
                    status = StatusCodes.Status409Conflict,
                    error = "UniqueConstraintViolation",
                    message = friendlyMessage,
                    detail = friendlyMessage,
                    instance = requestPath,
                    traceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier
                };
                await context.Response.WriteAsync(JsonSerializer.Serialize(conflictPayload));
                return;
            }

            if (sqlState == "23503") // foreign_key_violation
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                var fkPayload = new
                {
                    type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                    title = "Bad Request",
                    status = StatusCodes.Status400BadRequest,
                    error = "ForeignKeyViolation",
                    message = "La operación referencia un registro inexistente o infringe una restricción de integridad.",
                    detail = "La operación referencia un registro inexistente o infringe una restricción de integridad.",
                    instance = requestPath,
                    sqlState = sqlState
                };
                await context.Response.WriteAsync(JsonSerializer.Serialize(fkPayload));
                return;
            }

            if (sqlState == "22001" || sqlState == "23514") // string truncation or check violation
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                var constraintPayload = new
                {
                    type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                    title = "Bad Request",
                    status = StatusCodes.Status400BadRequest,
                    error = "InvalidDataConstraintViolation",
                    message = "Los datos proporcionados violan restricciones de formato o longitud en la base de datos.",
                    detail = "Los datos proporcionados violan restricciones de formato o longitud en la base de datos.",
                    instance = requestPath,
                    sqlState = sqlState
                };
                await context.Response.WriteAsync(JsonSerializer.Serialize(constraintPayload));
                return;
            }

            // Connection or Authentication failures
            if (sqlState.StartsWith("08") || sqlState == "28P01")
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                string detailMsg = sqlState == "28P01"
                    ? "Fallo de autenticación en PostgreSQL (Usuario/Contraseña incorrectos)."
                    : "Error de conexión con la base de datos PostgreSQL. Verifique que el servicio de base de datos esté activo y que las credenciales de conexión sean correctas.";

                var connPayload = new
                {
                    type = "https://tools.ietf.org/html/rfc7231#section-6.6.4",
                    title = "Service Unavailable",
                    status = StatusCodes.Status503ServiceUnavailable,
                    error = "DatabaseConnectionError",
                    message = detailMsg,
                    detail = detailMsg,
                    instance = requestPath,
                    sqlState = sqlState
                };
                await context.Response.WriteAsync(JsonSerializer.Serialize(connPayload));
                return;
            }
        }

        // 2. Generic NpgsqlException (transport, socket, connection timeout)
        var npgsqlEx = FindException<NpgsqlException>(exception);
        if (npgsqlEx != null)
        {
            AppLogger.LogDbError(exception, $"Request: {requestPath}");
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            string message = "Error de conexión con la base de datos PostgreSQL. Verifique que el servicio de base de datos esté activo y que las credenciales de conexión sean correctas.";
            var dbErrorPayload = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.6.4",
                title = "Service Unavailable",
                status = StatusCodes.Status503ServiceUnavailable,
                error = "DatabaseConnectionError",
                message = message,
                detail = message,
                instance = requestPath,
                sqlState = (string?)null
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(dbErrorPayload));
            return;
        }

        // 3. Known domain & business exceptions
        if (exception is KeyNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            var notFoundPayload = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.4",
                title = "Not Found",
                status = StatusCodes.Status404NotFound,
                error = "NotFound",
                message = exception.Message,
                detail = exception.Message,
                instance = requestPath
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(notFoundPayload));
            return;
        }

        if (exception is UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var forbiddenPayload = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
                title = "Forbidden",
                status = StatusCodes.Status403Forbidden,
                error = "Forbidden",
                message = exception.Message,
                detail = exception.Message,
                instance = requestPath
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(forbiddenPayload));
            return;
        }

        if (exception is ArgumentException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            var badRequestPayload = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                title = "Bad Request",
                status = StatusCodes.Status400BadRequest,
                error = "BadRequest",
                message = exception.Message,
                detail = exception.Message,
                instance = requestPath,
                traceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(badRequestPayload));
            return;
        }

        if (exception is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            var concurrencyPayload = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.8",
                title = "Conflicto de Concurrencia",
                status = StatusCodes.Status409Conflict,
                error = "ConcurrencyConflict",
                message = "El registro fue modificado concurrentemente por otro usuario o proceso. Por favor recargue e intente nuevamente.",
                detail = "El registro fue modificado concurrentemente por otro usuario o proceso. Por favor recargue e intente nuevamente.",
                instance = requestPath,
                traceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(concurrencyPayload));
            return;
        }

        if (exception is InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            var invalidOpPayload = new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.8",
                title = "Conflicto de Operación",
                status = StatusCodes.Status409Conflict,
                error = "InvalidOperation",
                message = exception.Message,
                detail = exception.Message,
                instance = requestPath,
                traceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(invalidOpPayload));
            return;
        }

        // 4. Non-database unhandled internal exception
        AppLogger.LogCrash(exception, $"Unhandled Exception in Request: {requestPath}");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var errorPayload = new
        {
            type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
            title = "Internal Server Error",
            status = StatusCodes.Status500InternalServerError,
            error = "InternalServerError",
            message = "Ocurrió un error interno al procesar la solicitud.",
            detail = "Ocurrió un error interno no esperado al procesar la solicitud.",
            instance = requestPath,
            traceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier
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
