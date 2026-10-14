using Core.DTOs;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

/// <summary>
/// 8.179-T2 (S3): cliente de escritorio del árbol de categorías del catálogo. Contrato congelado en
/// T1: <c>GET /api/categories</c> devuelve el listado plano con ruta legible ("A > B > C"); las
/// mutaciones requieren rol Admin/Manager y la semántica de reemplazo del conjunto es server-side.
/// </summary>
public interface ICategoryService
{
    Task<List<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken = default);
    Task<CategoryDto> CreateAsync(CreateCategoryRequestDto request, CancellationToken cancellationToken = default);
    Task<CategoryDto> UpdateAsync(int id, UpdateCategoryRequestDto request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
