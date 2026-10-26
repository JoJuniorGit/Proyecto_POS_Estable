using System;
using Sales.Module.Interfaces;

namespace Sales.Module.DTOs;

/// <summary>
/// 8.202-H2 (S4): datos para persistir la respuesta idempotente de un adelanto de efectivo en la
/// misma transacción que crea la venta y los movimientos de gaveta. El serializador lo aporta el
/// llamador para conservar el contrato HTTP (incluida la hora local).
/// </summary>
public sealed record CashAdvanceIdempotency(
    string Key,
    string RequestPath,
    byte[] PayloadHash,
    int? UserId,
    Func<CashAdvanceResultDto, string> SerializeResponse);
