using System.Globalization;
using System.Linq;
using System.Text;
using Core.DTOs;
using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService
{
    private async Task<SupplierColumnMapping> UpsertColumnMappingAsync(
        int supplierId,
        SupplierColumnMappingDto? requestMapping,
        CancellationToken cancellationToken)
    {
        var mapping = await _context.SupplierColumnMappings
            .FirstOrDefaultAsync(candidate => candidate.SupplierId == supplierId, cancellationToken);

        if (requestMapping is null)
        {
            return mapping ?? throw new ArgumentException(
                "A confirmed column mapping is required for a supplier's first import.",
                nameof(requestMapping));
        }

        var nameColumnName = RequiredColumn(requestMapping.NameColumnName, nameof(requestMapping.NameColumnName));
        var quantityColumnName = RequiredColumn(requestMapping.QuantityColumnName, nameof(requestMapping.QuantityColumnName));
        var unitCostColumnName = RequiredColumn(requestMapping.UnitCostColumnName, nameof(requestMapping.UnitCostColumnName));

        mapping ??= new SupplierColumnMapping { SupplierId = supplierId };
        mapping.BarcodeColumnName = OptionalColumn(requestMapping.BarcodeColumnName);
        mapping.SupplierCodeColumnName = OptionalColumn(requestMapping.SupplierCodeColumnName);
        mapping.NameColumnName = nameColumnName;
        mapping.QuantityColumnName = quantityColumnName;
        mapping.UnitCostColumnName = unitCostColumnName;

        if (mapping.Id == 0)
        {
            _context.SupplierColumnMappings.Add(mapping);
        }

        return mapping;
    }

    private static string RequiredColumn(string? columnName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            throw new ArgumentException("Required column mappings cannot be empty.", parameterName);
        }

        return columnName.Trim();
    }

    private static string? OptionalColumn(string? columnName) =>
        string.IsNullOrWhiteSpace(columnName) ? null : columnName.Trim();

    private static string NormalizeSupplierRifOrNit(string? value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static string NormalizeSupplierName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSeparator = false;

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToUpperInvariant(character));
                pendingSeparator = false;
            }
            else if (builder.Length > 0)
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString();
    }
}
