using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Core.DTOs;
using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService
{
    private const int MaxColumnNameLength = 128;
    private const int MaxAnchorKeywordsLength = 500;
    private const int MaxFactorPatternLength = 250;
    private const int MaxReplacementRulesCount = 50;
    private const int MaxReplacementTokenLength = 50;

    public async Task<SupplierOcrTemplateDto?> GetSupplierOcrTemplateAsync(
        int supplierId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.SupplierOcrTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.SupplierId == supplierId, cancellationToken);

        return entity is null ? null : ToOcrTemplateDto(entity);
    }

    public async Task<SupplierOcrTemplateDto> SaveSupplierOcrTemplateAsync(
        int supplierId,
        SupplierOcrTemplateDto template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        var supplierExists = await _context.Suppliers
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == supplierId, cancellationToken);
        if (!supplierExists)
        {
            throw new KeyNotFoundException($"Supplier {supplierId} was not found.");
        }

        var entity = await _context.SupplierOcrTemplates
            .FirstOrDefaultAsync(candidate => candidate.SupplierId == supplierId, cancellationToken);

        var normalizedReplacements = NormalizeCharacterReplacements(template.CharacterReplacements);
        var replacementsJson = normalizedReplacements.Count > 0
            ? JsonSerializer.Serialize(normalizedReplacements)
            : null;

        entity ??= new SupplierOcrTemplate { SupplierId = supplierId };
        entity.RotationDegrees = double.IsFinite(template.RotationDegrees) ? Math.Clamp(template.RotationDegrees, -360d, 360d) : 0d;
        entity.ContrastFactor = double.IsFinite(template.ContrastFactor) && template.ContrastFactor > 0d
            ? Math.Clamp(template.ContrastFactor, 0.1d, 5.0d)
            : 1.0d;
        entity.BrightnessDelta = double.IsFinite(template.BrightnessDelta) ? Math.Clamp(template.BrightnessDelta, -255d, 255d) : 0d;
        entity.BinarizationThreshold = template.BinarizationThreshold is >= 1 and <= 254 ? template.BinarizationThreshold : 0;
        entity.RoiX = template.RoiX is double rx && double.IsFinite(rx) ? Math.Clamp(rx, 0d, 1d) : null;
        entity.RoiY = template.RoiY is double ry && double.IsFinite(ry) ? Math.Clamp(ry, 0d, 1d) : null;
        entity.RoiWidth = template.RoiWidth is double rw && double.IsFinite(rw) ? Math.Clamp(rw, 0d, 1d) : null;
        entity.RoiHeight = template.RoiHeight is double rh && double.IsFinite(rh) ? Math.Clamp(rh, 0d, 1d) : null;
        entity.HeaderAnchorKeywords = TruncateOptionalText(template.HeaderAnchorKeywords, MaxAnchorKeywordsLength);
        entity.FooterAnchorKeywords = TruncateOptionalText(template.FooterAnchorKeywords, MaxAnchorKeywordsLength);
        entity.ProductColumnPosition = template.ProductColumnPosition is >= 1 and <= 20 ? template.ProductColumnPosition : null;
        entity.QuantityColumnPosition = template.QuantityColumnPosition is >= 1 and <= 20 ? template.QuantityColumnPosition : null;
        entity.UnitCostColumnPosition = template.UnitCostColumnPosition is >= 1 and <= 20 ? template.UnitCostColumnPosition : null;
        entity.SupplierCodeColumnPosition = template.SupplierCodeColumnPosition is >= 1 and <= 20 ? template.SupplierCodeColumnPosition : null;
        entity.DecimalSeparator = Enum.IsDefined(template.DecimalSeparator) ? template.DecimalSeparator : OcrDecimalSeparatorRule.Auto;
        entity.CostAmountRule = Enum.IsDefined(template.CostAmountRule) ? template.CostAmountRule : OcrCostAmountRule.UnitCost;
        entity.CharacterReplacementsJson = replacementsJson;
        entity.FactorExtractionPattern = TruncateOptionalText(template.FactorExtractionPattern, MaxFactorPatternLength);

        if (entity.Id == 0)
        {
            _context.SupplierOcrTemplates.Add(entity);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return ToOcrTemplateDto(entity);
    }

    internal static SupplierOcrTemplateDto ToOcrTemplateDto(SupplierOcrTemplate entity)
    {
        IReadOnlyList<OcrCharacterReplacementRuleDto>? replacements = null;
        if (!string.IsNullOrWhiteSpace(entity.CharacterReplacementsJson))
        {
            try
            {
                replacements = JsonSerializer.Deserialize<OcrCharacterReplacementRuleDto[]>(entity.CharacterReplacementsJson);
            }
            catch (JsonException)
            {
                replacements = null;
            }
        }

        return new SupplierOcrTemplateDto(
            entity.RotationDegrees,
            entity.ContrastFactor,
            entity.BrightnessDelta,
            entity.BinarizationThreshold,
            entity.RoiX,
            entity.RoiY,
            entity.RoiWidth,
            entity.RoiHeight,
            entity.HeaderAnchorKeywords,
            entity.FooterAnchorKeywords,
            entity.ProductColumnPosition,
            entity.QuantityColumnPosition,
            entity.UnitCostColumnPosition,
            entity.SupplierCodeColumnPosition,
            entity.CostAmountRule,
            entity.DecimalSeparator,
            replacements,
            entity.FactorExtractionPattern);
    }

    private static IReadOnlyList<OcrCharacterReplacementRuleDto> NormalizeCharacterReplacements(
        IReadOnlyList<OcrCharacterReplacementRuleDto>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return Array.Empty<OcrCharacterReplacementRuleDto>();
        }

        var normalized = new List<OcrCharacterReplacementRuleDto>(Math.Min(rules.Count, MaxReplacementRulesCount));
        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrEmpty(rule.From))
            {
                continue;
            }

            var from = rule.From.Length > MaxReplacementTokenLength
                ? rule.From[..MaxReplacementTokenLength]
                : rule.From;
            var to = (rule.To ?? string.Empty).Length > MaxReplacementTokenLength
                ? rule.To![..MaxReplacementTokenLength]
                : (rule.To ?? string.Empty);

            normalized.Add(new OcrCharacterReplacementRuleDto(from, to));
            if (normalized.Count >= MaxReplacementRulesCount)
            {
                break;
            }
        }

        return normalized;
    }

    private static string? TruncateOptionalText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

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
        var barcodeColumnName = OptionalColumnMapping(requestMapping.BarcodeColumnName, nameof(requestMapping.BarcodeColumnName));
        var supplierCodeColumnName = OptionalColumnMapping(requestMapping.SupplierCodeColumnName, nameof(requestMapping.SupplierCodeColumnName));

        mapping ??= new SupplierColumnMapping { SupplierId = supplierId };
        mapping.BarcodeColumnName = barcodeColumnName;
        mapping.SupplierCodeColumnName = supplierCodeColumnName;
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

        var trimmed = columnName.Trim();
        if (trimmed.Length > MaxColumnNameLength)
        {
            throw new ArgumentException($"Column mapping names cannot exceed {MaxColumnNameLength} characters.", parameterName);
        }

        return trimmed;
    }

    private static string? OptionalColumnMapping(string? columnName, string parameterName)
    {
        var trimmed = OptionalColumn(columnName);
        if (trimmed is { Length: > MaxColumnNameLength })
        {
            throw new ArgumentException($"Column mapping names cannot exceed {MaxColumnNameLength} characters.", parameterName);
        }

        return trimmed;
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
