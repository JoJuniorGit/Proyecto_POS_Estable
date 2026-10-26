using System;

namespace Core.DTOs;

/// <summary>
/// 8.203: ventana de idempotencia server-side para la edición masiva de precios. La clave se
/// deriva del payload y del actor; el servicio la reserva en la misma transacción del apply.
/// </summary>
public sealed record BulkPriceIdempotency(
    string Key,
    string RequestPath,
    byte[] PayloadHash,
    int? UserId,
    TimeSpan Ttl);
