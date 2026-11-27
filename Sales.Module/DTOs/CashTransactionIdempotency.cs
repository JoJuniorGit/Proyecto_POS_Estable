using System;

namespace Sales.Module.DTOs;

/// <summary>
/// 8.202-H2 (S4): datos para persistir la respuesta idempotente de un movimiento manual dentro de
/// la MISMA transacción que crea el <c>CashTransaction</c>. El serializador lo aporta el llamador
/// para conservar el contrato HTTP (incluida la hora local) sin acoplar el servicio al transporte.
/// </summary>
public sealed record CashTransactionIdempotency(
    string Key,
    string RequestPath,
    byte[] PayloadHash,
    int? UserId,
    Func<CashTransactionResponseDto, string> SerializeResponse);
