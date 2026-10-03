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

    private async Task<ProductMatch> MatchProductAsync(
        int supplierId,
        StageLineDto line,
        double similarityThreshold,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(line.Barcode))
        {
            var barcodeProduct = await _context.Products
                .AsNoTracking()
                .Where(product => !product.IsDeleted && product.SKU == line.Barcode)
                .OrderBy(product => product.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (barcodeProduct is not null)
            {
                return new ProductMatch(barcodeProduct, MatchMethod.Barcode);
            }
        }

        if (!string.IsNullOrWhiteSpace(line.SupplierCode))
        {
            var supplierCodeProduct = await (
                    from supplierCode in _context.SupplierProductCodes.AsNoTracking()
                    join product in _context.Products.AsNoTracking()
                        on supplierCode.ProductId equals product.Id
                    where supplierCode.SupplierId == supplierId
                        && supplierCode.Code == line.SupplierCode
                        && !product.IsDeleted
                    orderby product.Id
                    select product)
                .FirstOrDefaultAsync(cancellationToken);

            if (supplierCodeProduct is not null)
            {
                return new ProductMatch(supplierCodeProduct, MatchMethod.SupplierCode);
            }
        }

        if (string.IsNullOrWhiteSpace(line.Name))
        {
            return new ProductMatch(null, MatchMethod.None);
        }

        var candidates = await _similaritySearch.FindCandidatesAsync(
            line.Name,
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

    private static SupplierInvoiceLine CreateStagedLine(StageLineDto line, ProductMatch match)
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
            ? SupplierInvoiceLineStatus.Conflict
            : product.CostPriceUSD == 0m
                ? SupplierInvoiceLineStatus.New
                : line.UnitCostUSD != product.CostPriceUSD
                    ? SupplierInvoiceLineStatus.Update
                    : SupplierInvoiceLineStatus.Unchanged;

        return new SupplierInvoiceLine
        {
            SupplierCode = line.SupplierCode,
            Barcode = line.Barcode,
            Name = line.Name,
            Quantity = line.Quantity,
            UnitCostUSD = line.UnitCostUSD,
            Status = status,
            ResolvedProductId = product?.Id,
            OldCostPriceUSD = product?.CostPriceUSD,
            OldProfitMarginRetail = product?.ProfitMarginRetail,
            OldProfitMarginWholesale = product?.ProfitMarginWholesale,
            OldStockQuantity = product?.StockQuantity,
            MarginRetailOverride = retailMargin,
            MarginWholesaleOverride = wholesaleMargin,
            SuggestedRetailPriceUSD = retailMargin is decimal retail
                ? Core.Helpers.PricingCalculator.RoundPriceUp(line.UnitCostUSD * (1m + retail / 100m))
                : null,
            SuggestedWholesalePriceUSD = wholesaleMargin is decimal wholesale
                ? Core.Helpers.PricingCalculator.RoundPriceUp(line.UnitCostUSD * (1m + wholesale / 100m))
                : null,
            MatchMethod = match.Method
        };
    }

    private sealed record ProductMatch(Product? Product, MatchMethod Method);
}

public sealed class PostgresSupplierProductSimilaritySearch : ISupplierProductSimilaritySearch
{
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
            .Select(candidate => new SupplierProductSimilarityCandidate(candidate.Id, candidate.Similarity))
            .ToListAsync(cancellationToken);
    }
}
