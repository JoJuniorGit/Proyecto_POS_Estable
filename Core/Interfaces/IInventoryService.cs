using Core.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface IInventoryService
{
    Task<List<Product>> GetAllProductsAsync();
    Task<Product?> GetProductByIdAsync(int id);
    Task<Product?> GetProductBySkuAsync(string sku);
    Task<Product> CreateProductAsync(Product product);
    Task UpdateProductAsync(Product product);
    Task SetProductStatusAsync(int id, bool isActive, bool isDeleted);
    Task<string> DeleteProductAsync(int id, bool forceHardDelete = false);
    Task RestoreProductAsync(int id);
    Task UpdateStockAsync(int productId, int quantityChange, string reason); // +/- quantity
    Task<int> ReserveStockAsync(int productId, int quantity, TimeSpan duration);
    Task ConfirmReservationAsync(int reservationId, string reason);
    Task CancelReservationAsync(int reservationId);
    Task<Core.DTOs.ProductQuickInfoDto?> GetProductQuickInfoAsync(string sku);
    Task<List<Core.DTOs.ProductQuickInfoDto>> GetSuggestionsAsync(string filter, bool activeOnly, System.Threading.CancellationToken token);
    Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetProductsPagedAsync(string? filter, int page, int pageSize, string? statusFilter = null, System.Threading.CancellationToken token = default);

    Task<(int added, int updated)> BulkImportProductsAsync(IEnumerable<Core.DTOs.ProductImportDto> products, bool overwriteMerge, System.Threading.CancellationToken cancellationToken = default);
    Task<byte[]> ExportProductsAsync(string format, bool activeOnly, string? filter = null, System.Threading.CancellationToken cancellationToken = default);
    Task<byte[]> GenerateTemplateAsync(string format, System.Threading.CancellationToken cancellationToken = default);
}
