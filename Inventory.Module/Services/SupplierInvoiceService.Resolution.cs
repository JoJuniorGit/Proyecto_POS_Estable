using Core.Entities;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService
{
    /// <summary>
    /// 8.175-S5: reconcilia los slots de resolución de una línea (ancla + filas 1:N) contra el
    /// conjunto de productos objetivo. Las filas reemplazadas se validan zero-trust (deben
    /// pertenecer a la factura, no ser el ancla, estar en cantidad 0 y ya resueltas), se
    /// reutilizan en orden ascendente de Id y las sobrantes se eliminan. Los slots faltantes se
    /// crean con la misma plantilla de fila extra que usaba el loop original (código de proveedor,
    /// costo documental, factor de conversión y confianzas OCR del ancla).
    /// </summary>
    private List<SupplierInvoiceLine> PrepareResolutionSlots(
        SupplierInvoice invoice,
        SupplierInvoiceLine anchorLine,
        IReadOnlyList<int>? replacedLineIds,
        int targetCount)
    {
        var extras = new List<SupplierInvoiceLine>();
        if (replacedLineIds is { Count: > 0 })
        {
            var seenIds = new HashSet<int>();
            foreach (var replacedLineId in replacedLineIds)
            {
                if (!seenIds.Add(replacedLineId))
                {
                    continue;
                }

                var replacedLine = invoice.Lines.FirstOrDefault(candidate => candidate.Id == replacedLineId)
                    ?? throw new KeyNotFoundException(
                        $"Supplier invoice line {replacedLineId} was not found in invoice {invoice.Id}.");

                if (replacedLine.Id == anchorLine.Id)
                {
                    throw new ArgumentException(
                        $"Supplier invoice line {replacedLineId} cannot be replaced because it is the resolution anchor.");
                }

                if (replacedLine.Quantity != 0m)
                {
                    throw new ArgumentException(
                        $"Supplier invoice line {replacedLineId} has a quantity and cannot be replaced.");
                }

                if (replacedLine.ResolvedProductId is null)
                {
                    throw new ArgumentException(
                        $"Supplier invoice line {replacedLineId} is not resolved and cannot be replaced.");
                }

                extras.Add(replacedLine);
            }

            extras.Sort((left, right) => left.Id.CompareTo(right.Id));
        }

        var slots = new List<SupplierInvoiceLine>(targetCount) { anchorLine };
        for (var index = 1; index < targetCount; index++)
        {
            if (extras.Count > 0)
            {
                slots.Add(extras[0]);
                extras.RemoveAt(0);
                continue;
            }

            var createdLine = new SupplierInvoiceLine
            {
                SupplierInvoiceId = invoice.Id,
                SupplierCode = anchorLine.SupplierCode,
                Quantity = 0m,
                UnitCostDocument = anchorLine.UnitCostDocument,
                ConversionFactor = anchorLine.ConversionFactor,
                OcrNameConfidence = anchorLine.OcrNameConfidence,
                OcrQuantityConfidence = anchorLine.OcrQuantityConfidence,
                OcrUnitCostConfidence = anchorLine.OcrUnitCostConfidence
            };
            invoice.Lines.Add(createdLine);
            slots.Add(createdLine);
        }

        // Filas absorbidas que no caben en el grupo destino: se eliminan de la factura.
        foreach (var leftover in extras)
        {
            _context.SupplierInvoiceLines.Remove(leftover);
            invoice.Lines.Remove(leftover);
        }

        return slots;
    }
}
