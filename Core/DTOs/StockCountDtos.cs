using System;
using System.Collections.Generic;
using Core.Entities;

namespace Core.DTOs;

/// <summary>
/// 8.180-T1 (S2/S3): contrato de las sesiones de conteo físico ciego. Los DTOs de sesión/ítem
/// NUNCA exponen existencia teórica ni costo (modo ciego); el descuadre vive únicamente en
/// <see cref="StockCountReviewDto"/> (solo Admin/Manager).
/// </summary>
public sealed record StockCountSessionDto(
    int Id,
    string Name,
    StockCountStatus Status,
    int? ScopeCategoryId,
    string? ScopeCategoryPath,
    int ItemsCount,
    string? CreatedByUserId,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    DateTime? AppliedAt);

/// <summary>
/// 8.180-T1 (S3): ítem ciego para la vista de conteo — producto + cantidad contada + timestamps.
/// CIEGO por contrato: sin <c>TheoreticalStock</c> ni <c>UnitCostUSD</c> (los expone solo el review).
/// </summary>
public sealed record StockCountItemDto(
    int ProductId,
    string SKU,
    string ProductName,
    decimal CountedQuantity,
    DateTime UpdatedAt);

/// <summary>8.180-T1 (S2): detalle ciego de una sesión (ítems ordenados por nombre de producto).</summary>
public sealed record StockCountDetailDto(
    StockCountSessionDto Session,
    IReadOnlyList<StockCountItemDto> Items);

/// <summary>
/// 8.180-T1 (S4): fila del descuadre por ítem. <see cref="TheoreticalStock"/> es la existencia
/// theoretical del momento (LIVE si la sesión está Open, marcada por
/// <see cref="TheoreticalIsPreview"/>; congelada al cerrar en cualquier otro estado).
/// <see cref="Difference"/> = contado − teórico; <see cref="DifferenceValueUsd"/> = Δ × costo,
/// redondeado fiscalmente a 2 decimales (AwayFromZero).
/// </summary>
public sealed record StockCountReviewItemDto(
    int ProductId,
    string SKU,
    string ProductName,
    decimal CountedQuantity,
    decimal TheoreticalStock,
    decimal Difference,
    decimal UnitCostUSD,
    decimal DifferenceValueUsd,
    bool TheoreticalIsPreview);

/// <summary>
/// 8.180-T1 (S4): descuadre de la sesión. Los totales de faltantes llevan magnitud POSITIVA;
/// <see cref="NetValueUsd"/> = sobrantes − faltantes. <see cref="UnscannedInScopeCount"/> cuenta
/// los productos contables del alcance que no fueron escaneados (mínimo 0; supuesto L2: el conteo
/// rotativo parcial no aplica ajustes a los no escaneados).
/// </summary>
public sealed record StockCountReviewDto(
    int SessionId,
    StockCountStatus Status,
    bool TheoreticalIsPreview,
    IReadOnlyList<StockCountReviewItemDto> Items,
    decimal TotalSurplusQuantity,
    decimal TotalShortageQuantity,
    decimal TotalSurplusValueUsd,
    decimal TotalShortageValueUsd,
    decimal NetValueUsd,
    int UnscannedInScopeCount);

/// <summary>8.180-T1 (S2): alta de sesión (Admin/Manager). <c>ScopeCategoryId</c> null = todo el inventario.</summary>
public sealed record CreateStockCountRequestDto(
    string Name,
    int? ScopeCategoryId = null,
    string? Notes = null);

/// <summary>
/// 8.180-T1 (S2): escaneo por SKU/código de barras. <c>QuantityDelta</c> suma al ítem existente
/// (default +1); permite atajos de +N sin pasar por el set absoluto.
/// </summary>
public sealed record ScanStockCountRequestDto(
    string Code,
    decimal QuantityDelta = 1m);

/// <summary>8.180-T1 (S2): corrección absoluta de la cantidad contada de un producto (≥ 0).</summary>
public sealed record SetCountedQuantityRequestDto(decimal CountedQuantity);
