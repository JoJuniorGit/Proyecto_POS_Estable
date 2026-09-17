using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

public static class ApiProblemResults
{
    public static BadRequestObjectResult ApiBadRequest(this ControllerBase controller, string message, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status400BadRequest, "Bad Request", message, detail));

    public static NotFoundObjectResult ApiNotFound(this ControllerBase controller, string message, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status404NotFound, "Not Found", message, detail));

    public static ConflictObjectResult ApiConflict(this ControllerBase controller, string message, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status409Conflict, "Conflict", message, detail));

    public static ObjectResult ApiForbidden(this ControllerBase controller, string message, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status403Forbidden, "Forbidden", message, detail))
        {
            StatusCode = StatusCodes.Status403Forbidden
        };

    public static ObjectResult ApiUnprocessableEntity(this ControllerBase controller, string message, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status422UnprocessableEntity, "Unprocessable Entity", message, detail))
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity
        };

    private static ProblemDetails ProblemDetails(ControllerBase controller, int statusCode, string title, string message, string? detail)
    {
        var httpContext = controller.HttpContext;
        return new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail ?? message,
            Type = $"https://httpstatuses.com/{statusCode}",
            Instance = httpContext?.Request.Path.Value,
            Extensions =
            {
                ["message"] = message,
                ["traceId"] = Activity.Current?.Id ?? httpContext?.TraceIdentifier
            }
        };
    }
}