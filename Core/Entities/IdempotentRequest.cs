using System;

namespace Core.Entities;

/// <summary>
/// Representa una solicitud HTTP idempotente registrada para prevenir ejecuciones duplicadas o inconsistencias por reintentos de red.
/// </summary>
public class IdempotentRequest
{
    public int Id { get; set; }

    /// <summary>
    /// Clave provista por el cliente en el encabezado Idempotency-Key (máx. 128 caracteres).
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Ruta del endpoint donde se procesó la solicitud (ej: /api/sales/123/complete).
    /// </summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>
    /// Hash criptográfico SHA-256 (32 bytes) del payload compuesto (Method + Path + Body).
    /// Almacenado como tipo nativo bytea en PostgreSQL.
    /// </summary>
    public byte[] PayloadHash { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Código HTTP generado por la ejecución original (ej: 200, 201).
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Cuerpo de la respuesta original serializado en formato JSON.
    /// </summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>
    /// Fecha y hora UTC en que se registró la operación.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Fecha y hora UTC tras la cual el registro puede ser purgado (por defecto 24 horas tras su creación).
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
}
