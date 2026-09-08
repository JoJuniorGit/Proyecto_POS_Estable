using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

/// <summary>
/// Produce respuestas de error con el mismo shape RFC 7807 que GlobalExceptionHandlerMiddleware
/// (type/title/status/error/message/detail/instance/traceId). Los controllers DEBEN usar estos
/// helpers en lugar de "return BadRequest(new { Message = ex.Message })" para no filtrar detalle
/// interno ni romper el contrato de errores del cliente.
/// </summary>
public static class ApiProblemResults
{
    public static BadRequestObjectResult ApiBadRequest(this ControllerBase controller, string message, string? detail = null)
        => new BadRequestObjectResult(ProblemPayload(controller, StatusCodes.Status400BadRequest, "Bad Request", "BadRequest", message, detail));

    public static NotFoundObjectResult ApiNotFound(this ControllerBase controller, string message, string? detail = null)
        => new NotFoundObjectResult(ProblemPayload(controller, StatusCodes.Status404NotFound, "Not Found", "NotFound", message, detail));

    public static ConflictObjectResult ApiConflict(this ControllerBase controller, string message, string? detail = null)
        => new ConflictObjectResult(ProblemPayload(controller, StatusCodes.Status409Conflict, "Conflicto de Operación", "InvalidOperation", message, detail));

    public static ObjectResult ApiForbidden(this ControllerBase controller, string message, string? detail = null)
        => new ObjectResult(ProblemPayload(controller, StatusCodes.Status403Forbidden, "Forbidden", "Forbidden", message, detail))
        {
            StatusCode = StatusCodes.Status403Forbidden
        };

    public static ObjectResult ApiUnprocessableEntity(this ControllerBase controller, string message, string? detail = null)
        => new ObjectResult(ProblemPayload(controller, StatusCodes.Status422UnprocessableEntity, "Unprocessable Entity", "IdempotencyMismatch", message, detail))
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity
        };

    private static Dictionary<string, object?> ProblemPayload(ControllerBase controller, int statusCode, string title, string error, string message, string? detail)
    {
        var httpContext = controller.HttpContext;
        return new Dictionary<string, object?>
        {
            ["type"] = error,
            ["title"] = title,
            ["status"] = statusCode,
            ["error"] = error,
            ["message"] = message,
            ["detail"] = detail,
            ["instance"] = httpContext?.Request.Path.Value,
            ["traceId"] = Activity.Current?.Id ?? httpContext?.TraceIdentifier
        };
    }
}