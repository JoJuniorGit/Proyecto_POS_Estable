using System;
using System.Collections.Generic;
using System.Linq;
using Core.Entities;
using Core.Interfaces;

namespace Inventory.Module.Services;

/// <summary>
/// 8.149-T1 (SRE-01): datos de producto que UpdateStockBatchAsync necesita para resolver cada
/// request a su producto destino (padre con stock compartido + factor de conversión).
/// </summary>
public sealed record StockDeductionProductInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SKU { get; init; } = string.Empty;
    public bool IsCashAdvance { get; init; }
    public decimal ConversionFactor { get; init; }
    public int? ParentId { get; init; }
    public bool ParentIsStockShared { get; init; }
    public string? ParentName { get; init; }
    public string? ParentSKU { get; init; }

    /// <summary>8.177-T1: costo USD del producto destino para el snapshot histórico del movimiento.</summary>
    public decimal CostPriceUSD { get; init; }
}

/// <summary>
/// 8.149-T1 (SRE-01): deducción ya resuelta a su producto destino y consolidada por
/// (producto destino, razón, venta). 8.177-T1: además transporta los campos de auditoría del
/// request para que el movimiento único del grupo los persista.
/// </summary>
public sealed record ConsolidatedStockDeduction(
    int TargetProductId,
    decimal QuantityChange,
    string Reason,
    int? SaleId,
    StockMovementOriginType? OriginType = null,
    StockMovementReason? ReasonCode = null,
    string? AuthorizedByUserId = null,
    int? SupplierInvoiceId = null,
    string? Observation = null);

/// <summary>
/// 8.149-T1 (SRE-01): plan determinista de ejecución para un lote de deducciones de stock.
/// </summary>
public sealed record StockDeductionPlan(IReadOnlyList<ConsolidatedStockDeduction> Deductions, IReadOnlyList<string> SkusToInvalidate);

/// <summary>
/// 8.149-T1 (SRE-01): resolutor + consolidador puro que da a UpdateStockBatchAsync un orden de lock
/// determinista: una deducción por (producto destino, razón, venta), ascendente por ProductId, para
/// que lotes concurrentes tomen los row locks en la misma secuencia. Los totales por producto destino
/// son idénticos al comportamiento legado para cualquier orden de entrada.
/// </summary>
public static class StockDeductionConsolidator
{
    public static StockDeductionPlan ResolveAndConsolidate(
        IEnumerable<StockDeductionRequest>? requests,
        IReadOnlyDictionary<int, StockDeductionProductInfo> productsData)
    {
        ArgumentNullException.ThrowIfNull(productsData);

        var resolved = new List<ConsolidatedStockDeduction>();
        var skusToInvalidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in requests ?? Enumerable.Empty<StockDeductionRequest>())
        {
            // Contrato legado: QuantityChange == 0 es no-op y no debe fallar la resolución del producto.
            if (item.QuantityChange == 0) continue;

            if (!productsData.TryGetValue(item.ProductId, out var productData))
            {
                throw new KeyNotFoundException($"Producto {item.ProductId} no encontrado");
            }

            if (productData.IsCashAdvance)
            {
                throw new InvalidOperationException($"El producto '{productData.Name}' es un servicio de adelanto de efectivo y no maneja inventario físico.");
            }

            int targetProductId = item.ProductId;
            decimal effectiveQuantityChange = item.QuantityChange;
            string movementReason = item.Reason;

            if (productData.ParentId.HasValue && productData.ParentIsStockShared)
            {
                targetProductId = productData.ParentId.Value;
                decimal factor = productData.ConversionFactor > 0 ? productData.ConversionFactor : 1.0000m;
                effectiveQuantityChange = InventoryService.ApplyConversion(item.QuantityChange, factor, item.QuantityChange < 0);
                movementReason = $"Variante: {productData.Name} (SKU: {productData.SKU}, Factor: {factor:G29}) | {item.Reason}";
            }

            resolved.Add(new ConsolidatedStockDeduction(
                targetProductId,
                effectiveQuantityChange,
                movementReason,
                item.SaleId,
                item.OriginType,
                item.ReasonCode,
                item.AuthorizedByUserId,
                item.SupplierInvoiceId,
                item.Observation));

            if (!string.IsNullOrWhiteSpace(productData.SKU)) skusToInvalidate.Add(productData.SKU);
            if (!string.IsNullOrWhiteSpace(productData.ParentSKU)) skusToInvalidate.Add(productData.ParentSKU);
        }

        var deductions = resolved
            .GroupBy(d => (d.TargetProductId, d.Reason, d.SaleId))
            .Select(g => new ConsolidatedStockDeduction(
                g.Key.TargetProductId,
                g.Sum(d => d.QuantityChange),
                g.Key.Reason,
                g.Key.SaleId,
                // 8.177-T1: el grupo comparte clave (producto, razón, venta); si algún request del
                // grupo trae un campo de auditoría y otro no, gana el primer valor no nulo
                // (determinista por el orden de entrada).
                g.Select(d => d.OriginType).FirstOrDefault(value => value.HasValue),
                g.Select(d => d.ReasonCode).FirstOrDefault(value => value.HasValue),
                g.Select(d => d.AuthorizedByUserId).FirstOrDefault(value => !string.IsNullOrEmpty(value)),
                g.Select(d => d.SupplierInvoiceId).FirstOrDefault(value => value.HasValue),
                g.Select(d => d.Observation).FirstOrDefault(value => !string.IsNullOrEmpty(value))))
            .Where(d => d.QuantityChange != 0) // Un grupo que netea 0 no aplica delta: sin update ni movimiento.
            .OrderBy(d => d.TargetProductId)
            .ThenBy(d => d.Reason, StringComparer.Ordinal)
            .ThenBy(d => d.SaleId)
            .ToList();

        return new StockDeductionPlan(deductions, skusToInvalidate.ToList());
    }
}
