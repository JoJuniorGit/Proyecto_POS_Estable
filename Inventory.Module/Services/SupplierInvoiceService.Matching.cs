using System.Globalization;
using System.Linq;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService
{
    private const string SimilarityThresholdSettingKey = "SupplierInvoice.ProductMatchSimilarityThreshold";
    private const double DefaultSimilarityThreshold = 0.30d;

    private async Task<double> ReadSimilarityThresholdAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuredThreshold = await _systemSettingsService.GetSettingAsync(SimilarityThresholdSettingKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(configuredThreshold))
        {
            return DefaultSimilarityThreshold;
        }

        if (!double.TryParse(configuredThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            || !double.IsFinite(threshold)
            || threshold is < 0d or > 1d)
        {
            throw new InvalidOperationException(
                $"The system setting {SimilarityThresholdSettingKey} must be a number between 0 and 1.");
        }

        return threshold;
    }

    /// <summary>
    /// 8.155/PEF-01 (REQ-SIB-01): precarga única por stage de los caminos exactos del matching.
    /// Las claves son los valores ya normalizados por <c>ValidateAndNormalizeLines</c>
    /// (<c>OptionalColumn</c>: trim o null), exactamente los mismos que se usan como predicado
    /// en cada consulta; una línea por barcode/supplierCode se resuelve luego en memoria.
    /// </summary>
    private async Task<MatchLookups> LoadMatchLookupsAsync(
        int supplierId,
        IReadOnlyList<StageLineDto> lines,
        CancellationToken cancellationToken)
    {
        var barcodes = lines
            .Select(line => line.Barcode)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var supplierCodes = lines
            .Select(line => line.SupplierCode)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // "Primer producto por Id" para duplicados: se ordena por Id y se conserva el primero por
        // clave con TryAdd. Los índices únicos filtrados de producción no se aplican en arneses
        // InMemory, así que el desempate debe ser explícito aquí.
        var productsByBarcode = new Dictionary<string, Product>(StringComparer.Ordinal);
        if (barcodes.Length > 0)
        {
            var barcodeProducts = await _context.Products
                .AsNoTracking()
                .Where(product => !product.IsDeleted && barcodes.Contains(product.SKU))
                .OrderBy(product => product.Id)
                .ToListAsync(cancellationToken);

            foreach (var product in barcodeProducts)
            {
                productsByBarcode.TryAdd(product.SKU, product);
            }
        }

        var productsBySupplierCode = new Dictionary<string, Product>(StringComparer.Ordinal);
        if (supplierCodes.Length > 0)
        {
            var supplierCodeProducts = await (
                    from supplierCode in _context.SupplierProductCodes.AsNoTracking()
                    join product in _context.Products.AsNoTracking()
                        on supplierCode.ProductId equals product.Id
                    where supplierCode.SupplierId == supplierId
                        && supplierCodes.Contains(supplierCode.Code)
                        && !product.IsDeleted
                    orderby product.Id
                    select new { supplierCode.Code, Product = product })
                .ToListAsync(cancellationToken);

            foreach (var entry in supplierCodeProducts)
            {
                if (entry.Code is not null)
                {
                    productsBySupplierCode.TryAdd(entry.Code, entry.Product);
                }
            }
        }

        return new MatchLookups(productsByBarcode, productsBySupplierCode);
    }

    /// <summary>
    /// Prioridad barcode > supplierCode sobre los diccionarios precargados; null cuando no hay
    /// match exacto y corresponde evaluar el fallback fuzzy.
    /// </summary>
    private static ProductMatch? TryResolveExactMatch(MatchLookups lookups, StageLineDto line)
    {
        if (line.Barcode is not null
            && lookups.ProductsByBarcode.TryGetValue(line.Barcode, out var barcodeProduct))
        {
            return new ProductMatch(barcodeProduct, MatchMethod.Barcode);
        }

        if (line.SupplierCode is not null
            && lookups.ProductsBySupplierCode.TryGetValue(line.SupplierCode, out var supplierCodeProduct))
        {
            return new ProductMatch(supplierCodeProduct, MatchMethod.SupplierCode);
        }

        return null;
    }

    private async Task<ProductMatch> FallbackFuzzyMatchAsync(
        string? productName,
        double similarityThreshold,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return new ProductMatch(null, MatchMethod.None);
        }

        var candidates = await _similaritySearch.FindCandidatesAsync(
            productName,
            similarityThreshold,
            cancellationToken);
        var eligibleCandidates = candidates
            .Where(candidate => candidate.ProductId > 0
                && double.IsFinite(candidate.Similarity)
                && candidate.Similarity >= similarityThreshold
                && candidate.Similarity <= 1d)
            .OrderByDescending(candidate => candidate.Similarity)
            .ThenBy(candidate => candidate.ProductId)
            .ToList();

        if (eligibleCandidates.Count == 0)
        {
            return new ProductMatch(null, MatchMethod.None);
        }

        var candidateIds = eligibleCandidates
            .Select(candidate => candidate.ProductId)
            .Distinct()
            .ToArray();
        var productsById = await _context.Products
            .AsNoTracking()
            .Where(product => !product.IsDeleted && candidateIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        foreach (var candidate in eligibleCandidates)
        {
            if (productsById.TryGetValue(candidate.ProductId, out var product))
            {
                return new ProductMatch(product, MatchMethod.Fuzzy);
            }
        }

        return new ProductMatch(null, MatchMethod.None);
    }

    private static SupplierInvoiceLine CreateStagedLine(
        StageLineDto line,
        ProductMatch match,
        decimal unitCostUsd,
        bool ocrSourced)
    {
        var product = match.Product;
        decimal? retailMargin = product is null
            ? null
            : product.ProfitMarginRetail == 0m && product.ProfitPercentage > 0m
                ? product.ProfitPercentage
                : product.ProfitMarginRetail;
        decimal? wholesaleMargin = product is null
            ? null
            : product.HasWholesale
                ? product.ProfitMarginWholesale
                : retailMargin;

        var status = product is null
            ? SupplierInvoiceLineStatus.New
            : unitCostUsd != product.CostPriceUSD
                ? SupplierInvoiceLineStatus.Update
                : SupplierInvoiceLineStatus.Unchanged;

        return new SupplierInvoiceLine
        {
            SupplierCode = line.SupplierCode,
            Barcode = line.Barcode,
            Name = line.Name,
            Quantity = line.Quantity,
            UnitCostDocument = line.UnitCostDocument,
            UnitCostUSD = unitCostUsd,
            Status = status,
            ResolvedProductId = product?.Id,
            OldCostPriceUSD = product?.CostPriceUSD,
            OldProfitMarginRetail = product?.ProfitMarginRetail,
            OldProfitMarginWholesale = product?.ProfitMarginWholesale,
            OldStockQuantity = product?.StockQuantity,
            MarginRetailOverride = retailMargin,
            MarginWholesaleOverride = wholesaleMargin,
            SuggestedRetailPriceUSD = retailMargin is decimal retail
                ? Core.Helpers.PricingCalculator.RoundPriceUp(unitCostUsd * (1m + retail / 100m))
                : null,
            SuggestedWholesalePriceUSD = wholesaleMargin is decimal wholesale
                ? Core.Helpers.PricingCalculator.RoundPriceUp(unitCostUsd * (1m + wholesale / 100m))
                : null,
            MatchMethod = match.Method,
            // 8.147-S6/D10 + T9/D3: solo las líneas OCR persisten confianzas; las tabulares quedan
            // null y las OCR se acotan a 0–100 (las confianzas llegan de clientes no confiables).
            OcrNameConfidence = ocrSourced ? ClampOcrConfidence(line.OcrNameConfidence) : null,
            OcrQuantityConfidence = ocrSourced ? ClampOcrConfidence(line.OcrQuantityConfidence) : null,
            OcrUnitCostConfidence = ocrSourced ? ClampOcrConfidence(line.OcrUnitCostConfidence) : null
        };
    }

    /// <summary>
    /// 8.147-T9/D3: las confianzas OCR llegan de clientes no confiables; se acotan al rango
    /// porcentual 0–100 antes de persistirlas (null se conserva null).
    /// </summary>
    private static decimal? ClampOcrConfidence(decimal? confidence) =>
        confidence is null ? null : Math.Clamp(confidence.Value, 0m, 100m);

    private sealed record MatchLookups(
        Dictionary<string, Product> ProductsByBarcode,
        Dictionary<string, Product> ProductsBySupplierCode);

    private sealed record ProductMatch(Product? Product, MatchMethod Method);
}

public sealed class PostgresSupplierProductSimilaritySearch : ISupplierProductSimilaritySearch
{
    // 8.155/PEF-01 (REQ-SIB-02): cota del scan trigram. El orden (similitud desc, Id asc) se
    // aplica antes del Take, por lo que el ganador de la selección no cambia.
    private const int MaxSimilarityCandidates = 100;

    private readonly InventoryDbContext _context;

    public PostgresSupplierProductSimilaritySearch(InventoryDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<IReadOnlyList<SupplierProductSimilarityCandidate>> FindCandidatesAsync(
        string productName,
        double minimumSimilarity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        if (!double.IsFinite(minimumSimilarity) || minimumSimilarity is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSimilarity));
        }

        if (_context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            throw new InvalidOperationException("Product name similarity search requires PostgreSQL pg_trgm.");
        }

        return await _context.Products
            .AsNoTracking()
            .Where(product => !product.IsDeleted)
            .Select(product => new
            {
                product.Id,
                Similarity = EF.Functions.TrigramsSimilarity(product.Name, productName)
            })
            .Where(candidate => candidate.Similarity >= minimumSimilarity)
            .OrderByDescending(candidate => candidate.Similarity)
            .ThenBy(candidate => candidate.Id)
            .Take(MaxSimilarityCandidates)
            .Select(candidate => new SupplierProductSimilarityCandidate(candidate.Id, candidate.Similarity))
            .ToListAsync(cancellationToken);
    }
}
