using Core.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public interface IProductService
{
    Task<List<Product>> GetAllAsync();
    Task<Product?> GetByIdAsync(int id);
    Task<Product> CreateAsync(Product product);
    Task UpdateAsync(Product product);
    Task SetStatusAsync(int id, bool isActive, bool isDeleted);
    Task<string> DeleteAsync(int id, bool hardDelete = false);
    Task RestoreAsync(int id);
    Task AdjustStockAsync(int productId, decimal quantityChange, string reason);
    Task<Core.DTOs.ProductQuickInfoDto?> GetQuickInfoAsync(string sku);
    Task<List<Core.DTOs.ProductQuickInfoDto>> GetSuggestionsAsync(string filter, bool activeOnly, System.Threading.CancellationToken token);
    Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetPagedAsync(string? filter, int page, int pageSize, string? statusFilter = null, string? sortBy = null, bool isDescending = false, System.Threading.CancellationToken token = default);
    Task<List<Core.DTOs.ProductDto>> GetVariantsAsync(int parentProductId);
    Task<List<Core.DTOs.ProductDto>> GetParentsAsync();
}
