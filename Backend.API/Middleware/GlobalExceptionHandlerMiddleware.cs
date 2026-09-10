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
        // 8B-B2: RFC 7807 — el cuerpo ya es un problem+json (WriteProblemDetailsAsync).
        context.Response.ContentType = "application/problem+json";

        // 1. PostgreSQL specific exceptions (H-API-6 / H-API-20)
        var postgresEx = FindException<PostgresException>(exception);
        if (postgresEx != null)
        {
            AppLogger.LogDbError(exception, $"Request: {requestPath}");
            var sqlState = postgresEx.SqlState;

            if (sqlState == "23505") // unique_violation
            {
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

                await WriteProblemDetailsAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "Conflicto de Unicidad",
                    "https://tools.ietf.org/html/rfc7231#section-6.5.8",
                    "UniqueConstraintViolation",
                    friendlyMessage,
                    friendlyMessage,
                    requestPath,
                    sqlState);
                return;
            }

            if (sqlState == "23502") // not_null_violation
            {
                string columnInfo = string.IsNullOrWhiteSpace(postgresEx.ColumnName)
                    ? "Un campo requerido"
                    : $"El campo '{postgresEx.ColumnName}'";
                string friendlyMessage = $"{columnInfo} es obligatorio y no puede ser nulo.";

                await WriteProblemDetailsAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Bad Request",
                    "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                    "NotNullConstraintViolation",
                    friendlyMessage,
                    friendlyMessage,
                    requestPath,
                    sqlState);
                return;
            }

            if (sqlState == "23503") // foreign_key_violation
            {
                string friendlyMessage = "La operación referencia un registro inexistente o infringe una restricción de integridad referencial.";
                await WriteProblemDetailsAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Bad Request",
                    "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                    "ForeignKeyViolation",
                    friendlyMessage,
                    friendlyMessage,
                    requestPath,
                    sqlState);
                return;
            }

            if (sqlState == "22001" || sqlState == "23514") // string truncation or check violation
            {
                string friendlyMessage = "Los datos proporcionados violan restricciones de formato o longitud en la base de datos.";
                await WriteProblemDetailsAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Bad Request",
                    "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                    "InvalidDataConstraintViolation",
                    friendlyMessage,
                    friendlyMessage,
                    requestPath,
                    sqlState);
                return;
            }

            // Connection or Authentication failures
            if (sqlState.StartsWith("08") || sqlState == "28P01")
            {
                string detailMsg = sqlState == "28P01"
                    ? "Fallo de autenticación en PostgreSQL (Usuario/Contraseña incorrectos)."
                    : "Error de conexión con la base de datos PostgreSQL. Verifique que el servicio de base de datos esté activo y que las credenciales de conexión sean correctas.";

                await WriteProblemDetailsAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "Service Unavailable",
                    "https://tools.ietf.org/html/rfc7231#section-6.6.4",
                    "DatabaseConnectionError",
                    detailMsg,
                    detailMsg,
                    requestPath,
                    sqlState);
                return;
            }
        }

        // 2. Generic NpgsqlException (transport, socket, connection timeout)
        var npgsqlEx = FindException<NpgsqlException>(exception);
        if (npgsqlEx != null)
        {
            AppLogger.LogDbError(exception, $"Request: {requestPath}");
            string message = "Error de conexión con la base de datos PostgreSQL. Verifique que el servicio de base de datos esté activo y que las credenciales de conexión sean correctas.";
            await WriteProblemDetailsAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Service Unavailable",
                "https://tools.ietf.org/html/rfc7231#section-6.6.4",
                "DatabaseConnectionError",
                message,
                message,
                requestPath,
                null);
            return;
        }

        // 3. Request cancellation, client aborted: do not alarm
        if (exception is OperationCanceledException)
        {
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return;
        }

        // 3. Known domain & business exceptions
        if (exception is KeyNotFoundException)
        {
            await WriteProblemDetailsAsync(
                context,
                StatusCodes.Status404NotFound,
                "Not Found",
                "https://tools.ietf.org/html/rfc7231#section-6.5.4",
                "NotFound",
                exception.Message,
                exception.Message,
                requestPath,
                null);
            return;
        }

        if (exception is UnauthorizedAccessException)
        {
            await WriteProblemDetailsAsync(
                context,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                "https://tools.ietf.org/html/rfc7231#section-6.5.3",
                "Forbidden",
                exception.Message,
                exception.Message,
                requestPath,
                null);
            return;
        }

        if (exception is ArgumentException)
        {
            await WriteProblemDetailsAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Bad Request",
                "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                "BadRequest",
                exception.Message,
                exception.Message,
                requestPath,
                null);
            return;
        }

        if (exception is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            string msg = "El registro fue modificado concurrentemente por otro usuario o proceso. Por favor recargue e intente nuevamente.";
            await WriteProblemDetailsAsync(
                context,
                StatusCodes.Status409Conflict,
                "Conflicto de Concurrencia",
                "https://tools.ietf.org/html/rfc7231#section-6.5.8",
                "ConcurrencyConflict",
                msg,
                msg,
                requestPath,
                null);
            return;
        }

        if (exception is InvalidOperationException)
        {
            await WriteProblemDetailsAsync(
                context,
                StatusCodes.Status409Conflict,
                "Conflicto de Operación",
                "https://tools.ietf.org/html/rfc7231#section-6.5.8",
                "InvalidOperation",
                exception.Message,
                exception.Message,
                requestPath,
                null);
            return;
        }

        // 4. Non-database unhandled internal exception
        AppLogger.LogCrash(exception, $"Unhandled Exception in Request: {requestPath}");
        await WriteProblemDetailsAsync(
            context,
            StatusCodes.Status500InternalServerError,
            "Internal Server Error",
            "https://tools.ietf.org/html/rfc7231#section-6.6.1",
            "InternalServerError",
            "Ocurrió un error interno al procesar la solicitud.",
            "Ocurrió un error interno no esperado al procesar la solicitud.",
            requestPath,
            null);
    }

    private static async Task WriteProblemDetailsAsync(
        HttpContext context,
        int statusCode,
        string title,
        string type,
        string error,
        string message,
        string detail,
        string requestPath,
        string? sqlState = null)
    {
        context.Response.StatusCode = statusCode;
        var traceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;

        var payload = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["title"] = title,
            ["status"] = statusCode,
            ["error"] = error,
            ["message"] = message,
            ["detail"] = detail,
            ["instance"] = requestPath,
            ["traceId"] = traceId
        };

        if (!string.IsNullOrEmpty(sqlState))
        {
            payload["sqlState"] = sqlState;
        }

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
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
