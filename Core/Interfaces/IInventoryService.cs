using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface IInventoryService
{
    Task<decimal> GetTodayExchangeRateAsync(System.Threading.CancellationToken cancellationToken = default);
    void InvalidateTodayExchangeRateCache();
    Task<IReadOnlyList<Core.DTOs.SaleProductInfoDto>> GetSaleProductsByIdsAsync(IEnumerable<int> productIds, System.Threading.CancellationToken cancellationToken = default);
    Task<Core.DTOs.SaleProductInfoDto?> GetSaleProductByIdAsync(int id, System.Threading.CancellationToken cancellationToken = default);
    Task<Core.DTOs.SaleProductInfoDto?> GetCashAdvanceProductAsync(System.Threading.CancellationToken cancellationToken = default);
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    Task<Core.DTOs.ProductQuickInfoDto?> GetProductQuickInfoAsync(string sku, bool useCache = true, System.Threading.CancellationToken cancellationToken = default);
    void InvalidateProductSkuCache(string sku);
    void InvalidateAllProductCaches();
    Task<int> CreateSystemProductAsync(Core.DTOs.CreateSystemProductRequest request, System.Threading.CancellationToken cancellationToken = default);
    Task SetProductStatusAsync(int id, bool isActive, bool isDeleted, System.Threading.CancellationToken cancellationToken = default);
    Task<string> DeleteProductAsync(int id, bool forceHardDelete = false, System.Threading.CancellationToken cancellationToken = default);
    Task RestoreProductAsync(int id, System.Threading.CancellationToken cancellationToken = default);
    Task UpdateStockAsync(int productId, decimal quantityChange, string reason, string? userId = null, bool allowNegativeStock = false, System.Threading.CancellationToken cancellationToken = default);
    Task UpdateStockBatchAsync(IEnumerable<StockDeductionRequest> items, string? userId = null, bool allowNegativeStock = false, System.Threading.CancellationToken cancellationToken = default);
    Task AdjustStockAsync(int productId, decimal quantityChange, string reason, string? userId = null, System.Threading.CancellationToken cancellationToken = default);
    Task<int> ReserveStockAsync(int productId, decimal quantity, TimeSpan duration, string? referenceId = null, System.Threading.CancellationToken cancellationToken = default);
    Task ConfirmReservationAsync(int reservationId, string reason, System.Threading.CancellationToken cancellationToken = default);
    Task CancelReservationAsync(int reservationId, System.Threading.CancellationToken cancellationToken = default);
    Task<List<Core.DTOs.ProductQuickInfoDto>> GetSuggestionsAsync(string filter, bool activeOnly, System.Threading.CancellationToken token);
    Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetProductsPagedAsync(string? filter, int page, int pageSize, string? statusFilter = null, string? sortBy = null, bool isDescending = false, System.Threading.CancellationToken token = default);
    Task<List<Core.DTOs.ProductDto>> GetVariantOptionsAsync(int parentProductId, System.Threading.CancellationToken cancellationToken = default);
    Task<List<Core.DTOs.ProductDto>> GetParentProductsAsync(System.Threading.CancellationToken cancellationToken = default);
    Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetCandidateVariantsPagedAsync(int parentId, string? filter, int page, int pageSize, System.Threading.CancellationToken token = default);
    Task<List<Core.DTOs.ProductDto>> LinkVariantsBatchAsync(int parentId, List<int> productIds, System.Threading.CancellationToken token = default);
    Task<Core.DTOs.ProductDto> UnlinkVariantAsync(int parentId, int variantId, System.Threading.CancellationToken token = default);

    Task<(int added, int updated)> BulkImportProductsAsync(IEnumerable<Core.DTOs.ProductImportDto> products, bool overwriteMerge, System.Threading.CancellationToken cancellationToken = default);
    Task<byte[]> ExportProductsAsync(string format, bool activeOnly, string? filter = null, System.Threading.CancellationToken cancellationToken = default);
    Task<byte[]> GenerateTemplateAsync(string format, System.Threading.CancellationToken cancellationToken = default);
    Task EnrollInTransactionAsync(System.Data.Common.DbTransaction transaction, System.Threading.CancellationToken cancellationToken = default);
    /// <summary>Restaura la conexión propia del scope tras una transacción compartida (8.7-B7).</summary>
    Task DetachFromTransactionAsync(System.Threading.CancellationToken cancellationToken = default);
}

public record StockDeductionRequest(int ProductId, decimal QuantityChange, string Reason, int? SaleId = null);
