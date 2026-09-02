using Core.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface IInventoryService
{
    Task<List<Product>> GetAllProductsAsync();
    Task<decimal> GetTodayExchangeRateAsync();
    void InvalidateTodayExchangeRateCache();
    Task<List<Product>> GetProductsByIdsAsync(IEnumerable<int> productIds);
    Task<Product?> GetProductByIdAsync(int id);
    Task<Product?> GetCashAdvanceProductAsync();
    Task<Product?> GetProductBySkuAsync(string sku);
    Task<Product> CreateProductAsync(Product product);
    Task UpdateProductAsync(Product product);
    Task SetProductStatusAsync(int id, bool isActive, bool isDeleted);
    Task<string> DeleteProductAsync(int id, bool forceHardDelete = false);
    Task RestoreProductAsync(int id);
    Task UpdateStockAsync(int productId, decimal quantityChange, string reason, string? userId = null, bool allowNegativeStock = false); // +/- quantity
    Task AdjustStockAsync(int productId, decimal quantityChange, string reason, string? userId = null);
    Task<int> ReserveStockAsync(int productId, decimal quantity, TimeSpan duration);
    Task ConfirmReservationAsync(int reservationId, string reason);
    Task CancelReservationAsync(int reservationId);
    Task<Core.DTOs.ProductQuickInfoDto?> GetProductQuickInfoAsync(string sku);
    Task<List<Core.DTOs.ProductQuickInfoDto>> GetSuggestionsAsync(string filter, bool activeOnly, System.Threading.CancellationToken token);
    Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetProductsPagedAsync(string? filter, int page, int pageSize, string? statusFilter = null, string? sortBy = null, bool isDescending = false, System.Threading.CancellationToken token = default);
    Task<List<Core.DTOs.ProductDto>> GetVariantOptionsAsync(int parentProductId);
    Task<List<Core.DTOs.ProductDto>> GetParentProductsAsync();

    Task<(int added, int updated)> BulkImportProductsAsync(IEnumerable<Core.DTOs.ProductImportDto> products, bool overwriteMerge, System.Threading.CancellationToken cancellationToken = default);
    Task<byte[]> ExportProductsAsync(string format, bool activeOnly, string? filter = null, System.Threading.CancellationToken cancellationToken = default);
    Task<byte[]> GenerateTemplateAsync(string format, System.Threading.CancellationToken cancellationToken = default);
}
