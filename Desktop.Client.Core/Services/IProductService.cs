using Core.DTOs;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public interface IProductService
{
    Task<ProductDto?> GetByIdAsync(int id);
    Task<ProductDto> CreateAsync(CreateProductDto dto);
    Task UpdateAsync(UpdateProductDto dto);
    Task SetStatusAsync(int id, bool isActive, bool isDeleted);
    Task<string> DeleteAsync(int id, bool hardDelete = false);
    Task RestoreAsync(int id);
    Task AdjustStockAsync(int productId, decimal quantityChange, string reason, Core.Entities.StockMovementReason? reasonCode = null, string? observation = null);
    Task<Core.DTOs.ProductQuickInfoDto?> GetQuickInfoAsync(string sku);
    Task<List<Core.DTOs.ProductQuickInfoDto>> GetSuggestionsAsync(string filter, bool activeOnly, System.Threading.CancellationToken token);
    Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetPagedAsync(string? filter, int page, int pageSize, string? statusFilter = null, string? sortBy = null, bool isDescending = false, System.Threading.CancellationToken token = default, int? categoryId = null);
    Task<List<Core.DTOs.ProductDto>> GetVariantsAsync(int parentProductId);
    Task<List<Core.DTOs.ProductDto>> GetParentsAsync();
    Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetCandidateVariantsPagedAsync(int parentId, string? filter, int page, int pageSize, System.Threading.CancellationToken token = default);
    Task<List<Core.DTOs.ProductDto>> LinkVariantsBatchAsync(int parentId, List<int> productIds, System.Threading.CancellationToken token = default);
    Task<Core.DTOs.ProductDto> UnlinkVariantAsync(int parentId, int variantId, System.Threading.CancellationToken token = default);

    /// <summary>
    /// 8.181-T2 (S5): vista previa (sin escrituras) de la edición masiva de precios del catálogo.
    /// Comparte el contrato congelado con el apply (selección explícita o Filter+SelectAllFiltered
    /// y operaciones por campo).
    /// </summary>
    Task<Core.DTOs.BulkPriceUpdateResultDto> PreviewBulkPricesAsync(Core.DTOs.BulkPriceUpdateRequestDto request, System.Threading.CancellationToken token = default);

    /// <summary>8.181-T2 (S5): aplicación atómica (all-or-nothing) de la edición masiva de precios del catálogo.</summary>
    Task<Core.DTOs.BulkPriceUpdateResultDto> ApplyBulkPricesAsync(Core.DTOs.BulkPriceUpdateRequestDto request, System.Threading.CancellationToken token = default);
}
