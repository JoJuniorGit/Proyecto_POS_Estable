using System;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public class IdempotencyCheckResult
{
    public bool IsNew { get; set; }
    public bool IsReplay { get; set; }
    public bool IsMismatch { get; set; }
    public bool IsInProgressConflict { get; set; }
    public int StoredStatusCode { get; set; }
    public string? StoredResponseBody { get; set; }

    public static IdempotencyCheckResult New() => new() { IsNew = true };
    public static IdempotencyCheckResult Replay(int statusCode, string responseBody) => new() 
    { 
        IsReplay = true, 
        StoredStatusCode = statusCode, 
        StoredResponseBody = responseBody 
    };
    public static IdempotencyCheckResult Mismatch() => new() { IsMismatch = true };
    public static IdempotencyCheckResult Conflict() => new() { IsInProgressConflict = true };
}

public interface IIdempotencyService
{
    /// <summary>
    /// Valida que la clave de idempotencia cumpla con las reglas de longitud y formato seguro.
    /// </summary>
    bool ValidateKeyFormat(string? key, out string? errorMessage);

    /// <summary>
    /// Calcula el hash criptográfico SHA-256 (32 bytes) del payload compuesto por Method + Path + Body bytes.
    /// </summary>
    byte[] ComputePayloadHash(string method, string path, byte[] bodyBytes);

    /// <summary>
    /// Verifica si una petición previa ya fue registrada bajo la misma (Key, RequestPath).
    /// </summary>
    Task<IdempotencyCheckResult> CheckAsync(string key, string requestPath, byte[] payloadHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra el resultado exitoso de una operación dentro de la transacción activa de SalesDbContext.
    /// </summary>
    Task RegisterSuccessAsync(string key, string requestPath, byte[] payloadHash, int statusCode, string responseBody, CancellationToken cancellationToken = default);

    /// <summary>
    /// Telemetría: Cantidad de solicitudes resueltas directamente desde la caché de idempotencia.
    /// </summary>
    long Hits { get; }

    /// <summary>
    /// Telemetría: Cantidad de solicitudes nuevas procesadas.
    /// </summary>
    long Misses { get; }

    /// <summary>
    /// Telemetría: Cantidad de colisiones concurrentes o discrepancias de hash detectadas.
    /// </summary>
    long Conflicts { get; }
}
