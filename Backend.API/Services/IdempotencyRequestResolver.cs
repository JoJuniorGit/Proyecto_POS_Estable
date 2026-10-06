using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.Interfaces;
using Core.Logging;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Services;

/// <summary>
/// 8.149 (SRE-02): resolutor compartido de idempotencia para endpoints mutacionales.
/// Extraído del método privado de <see cref="Controllers.SalesController"/> para que los
/// endpoints de items, caja y cierre compartan exactamente la misma semántica:
/// 400 sin clave, replay con <c>X-Cache-Lookup: HIT</c> y 422 ante payload distinto.
/// </summary>
public sealed class IdempotencyRequestResolver
{
    public const string DefaultMissingKeyMessage = "El encabezado Idempotency-Key es obligatorio para esta operación.";

    // 8.7-M5: mensaje único de mismatch, compartido por el flujo normal y el de colisión.
    public const string PayloadMismatchMessage = "La clave de idempotencia ya fue utilizada para una transacción diferente con otro contenido.";

    private readonly IIdempotencyService? _idempotencyService;
    private readonly ICurrentUserService _currentUserService;

    public IdempotencyRequestResolver(IIdempotencyService? idempotencyService, ICurrentUserService currentUserService)
    {
        _idempotencyService = idempotencyService;
        _currentUserService = currentUserService;
    }

    public int? GetActorUserId()
    {
        return _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid)
            ? uid
            : null;
    }

    public async Task<IdempotencyResolution> ResolveAsync(
        ControllerBase controller,
        string requestPath,
        string bodyJson,
        string missingKeyMessage = DefaultMissingKeyMessage,
        bool parseNumericBodyAsInvoice = false)
    {
        string? idempotencyKey = controller.Request?.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new IdempotencyResolution(true, controller.ApiBadRequest(missingKeyMessage), null, null);
        }

        idempotencyKey = idempotencyKey.Trim();

        if (_idempotencyService == null)
        {
            return new IdempotencyResolution(false, null, idempotencyKey, null);
        }

        if (!_idempotencyService.ValidateKeyFormat(idempotencyKey, out var formatError))
        {
            return new IdempotencyResolution(true, controller.ApiBadRequest(formatError), null, null);
        }

        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(bodyJson);
        var payloadHash = _idempotencyService.ComputePayloadHash(controller.Request?.Method ?? "POST", requestPath, bodyBytes);

        var checkResult = await _idempotencyService.CheckAsync(idempotencyKey, requestPath, payloadHash, GetActorUserId(), controller.HttpContext?.RequestAborted ?? default);
        if (checkResult.IsReplay)
        {
            if (controller.Response?.Headers != null)
            {
                controller.Response.Headers["X-Cache-Lookup"] = "HIT";
            }
            if (parseNumericBodyAsInvoice && int.TryParse(checkResult.StoredResponseBody, out int cachedInvoice))
            {
                return new IdempotencyResolution(true, controller.Ok(cachedInvoice), null, null);
            }
            return new IdempotencyResolution(true, controller.Content(checkResult.StoredResponseBody ?? "", "application/json"), null, null);
        }

        if (checkResult.IsMismatch)
        {
            var clientIp = controller.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            AppLogger.LogSecurityAudit($"[IDEMPOTENCY_MISMATCH] Key={idempotencyKey}, Path={requestPath}, IP={clientIp}, Timestamp={DateTime.UtcNow:O}");
            return new IdempotencyResolution(true, controller.ApiUnprocessableEntity(PayloadMismatchMessage), null, null);
        }

        return new IdempotencyResolution(false, null, idempotencyKey, payloadHash);
    }

    public async Task<ActionResult> HandleCollisionAsync(
        ControllerBase controller,
        Microsoft.EntityFrameworkCore.DbUpdateException ex,
        string requestPath,
        string? key,
        byte[]? payloadHash,
        bool parseNumericBodyAsInvoice = false)
    {
        if (_idempotencyService is Sales.Module.Services.IdempotencyService idService && !string.IsNullOrWhiteSpace(key) && payloadHash != null)
        {
            var collisionResult = await idService.HandleConcurrentCollisionAsync(key, requestPath, payloadHash, GetActorUserId(), controller.HttpContext?.RequestAborted ?? default);
            if (collisionResult.IsReplay)
            {
                if (controller.Response?.Headers != null)
                {
                    controller.Response.Headers["X-Cache-Lookup"] = "HIT";
                }
                if (parseNumericBodyAsInvoice && int.TryParse(collisionResult.StoredResponseBody, out int cachedInvoice))
                {
                    return controller.Ok(cachedInvoice);
                }
                return controller.Content(collisionResult.StoredResponseBody ?? "", "application/json");
            }
            if (collisionResult.IsMismatch)
            {
                var clientIp = controller.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
                AppLogger.LogSecurityAudit($"[IDEMPOTENCY_MISMATCH] Key={key}, Path={requestPath}, IP={clientIp}, Timestamp={DateTime.UtcNow:O}");
                return controller.ApiUnprocessableEntity(PayloadMismatchMessage);
            }
        }
        return controller.ApiConflict("Operación concurrente en progreso para esta clave de idempotencia.");
    }

    public static bool IsIdempotencyUniqueViolation(Microsoft.EntityFrameworkCore.DbUpdateException ex)
    {
        return ex.Message.Contains("IX_IdempotentRequests")
            || ex.InnerException?.Message.Contains("IX_IdempotentRequests") == true
            || (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505");
    }

    /// <summary>
    /// Persiste la respuesta exitosa para que un reintento con la misma clave haga replay.
    /// No-op cuando no hay servicio de idempotencia inyectado o la resolución no trajo hash
    /// (modo degradado de tests/direct invocation).
    /// </summary>
    public async Task RegisterSuccessAsync(
        IdempotencyResolution resolution,
        string requestPath,
        string responseBody,
        CancellationToken cancellationToken = default)
    {
        if (_idempotencyService == null || string.IsNullOrWhiteSpace(resolution.Key) || resolution.PayloadHash == null)
        {
            return;
        }

        await _idempotencyService.RegisterSuccessAsync(resolution.Key, requestPath, resolution.PayloadHash, 200, responseBody, GetActorUserId(), cancellationToken);
    }
}

/// <summary>Resultado de resolver la idempotencia de una petición mutacional.</summary>
public readonly record struct IdempotencyResolution(bool ShouldStop, ActionResult? BlockingResult, string? Key, byte[]? PayloadHash);
