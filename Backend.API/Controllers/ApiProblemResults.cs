using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

public static class ApiProblemResults
{
    public static BadRequestObjectResult ApiBadRequest(this ControllerBase controller, string? message = null, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status400BadRequest, "Bad Request", message ?? "Solicitud inválida.", detail));

    public static NotFoundObjectResult ApiNotFound(this ControllerBase controller, string? message = null, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status404NotFound, "Not Found", message ?? "Recurso no encontrado.", detail));

    public static ConflictObjectResult ApiConflict(this ControllerBase controller, string? message = null, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status409Conflict, "Conflict", message ?? "Conflicto de estado.", detail));

    public static ObjectResult ApiForbidden(this ControllerBase controller, string? message = null, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status403Forbidden, "Forbidden", message ?? "Acceso denegado.", detail))
        {
            StatusCode = StatusCodes.Status403Forbidden
        };

    public static ObjectResult ApiPasswordChangeRequired(this ControllerBase controller, string? message = null, string? detail = null)
    {
        var pd = ProblemDetails(controller, StatusCodes.Status403Forbidden, "Forbidden", message ?? "Debe cambiar su contraseña antes de continuar.", detail);
        pd.Extensions["requiresPasswordChange"] = true;
        return new ObjectResult(pd)
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }

    public static UnauthorizedObjectResult ApiUnauthorized(this ControllerBase controller, string? message = null, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status401Unauthorized, "Unauthorized", message ?? "No autorizado.", detail));

    public static ObjectResult ApiUnprocessableEntity(this ControllerBase controller, string? message = null, string? detail = null)
        => new(ProblemDetails(controller, StatusCodes.Status422UnprocessableEntity, "Unprocessable Entity", message ?? "Entidad no procesable.", detail))
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity
        };

    public static ObjectResult ApiProblem(this ControllerBase controller, string? message = null, int statusCode = StatusCodes.Status500InternalServerError, string title = "Error", string? detail = null)
        => new(ProblemDetails(controller, statusCode, title, message ?? "Error inesperado.", detail))
        {
            StatusCode = statusCode
        };

    public static ActionResult ApiValidationProblem(this ControllerBase controller, Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState)
    {
        return controller.ValidationProblem(modelState);
    }

    private static ProblemDetails ProblemDetails(ControllerBase controller, int statusCode, string title, string message, string? detail)
    {
        var httpContext = controller.HttpContext;
        var pd = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail ?? message,
            Type = $"https://httpstatuses.com/{statusCode}",
            Instance = httpContext?.Request.Path.Value
        };
        pd.Extensions["message"] = message;
        pd.Extensions["traceId"] = Activity.Current?.Id ?? httpContext?.TraceIdentifier;
        return pd;
    }
}