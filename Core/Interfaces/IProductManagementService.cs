using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;

namespace Core.Interfaces;

public interface IProductManagementService
{
    Task<ProductDto> CreateProductFromDtoAsync(CreateProductDto request, CancellationToken cancellationToken = default);
    Task UpdateProductFromDtoAsync(int id, UpdateProductDto request, CancellationToken cancellationToken = default);
    Task<ProductDto?> GetProductDtoByIdAsync(int id, CancellationToken cancellationToken = default);
}
