using System.Collections.Generic;
using System.Linq;
using Core.Entities;

namespace Inventory.Module.Services;

/// <summary>
/// 8.154 (SRE-01) — orden determinista de adquisición de bloqueos de fila de Products (asc por
/// ResolvedProductId, nulls primero, tie-break Id); evita deadlocks AB-BA entre facturas con los
/// mismos productos en orden invertido.
/// </summary>
public static class SupplierInvoiceApplyOrdering
{
    /// <summary>
    /// Devuelve las mismas líneas en la secuencia que el loop de aplicación debe seguir: no altera
    /// el conjunto de efectos (mutaciones, movimientos, alias, estado), solo el orden de los locks.
    /// </summary>
    /// <param name="lines">Líneas de la factura de proveedor a procesar.</param>
    /// <returns>Las líneas ordenadas ascendentemente por ResolvedProductId y luego por Id.</returns>
    public static IReadOnlyList<SupplierInvoiceLine> OrderForApply(IEnumerable<SupplierInvoiceLine> lines)
        => lines.OrderBy(line => line.ResolvedProductId).ThenBy(line => line.Id).ToList();
}
