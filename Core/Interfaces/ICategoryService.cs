using Core.DTOs;

namespace Core.Interfaces;

/// <summary>
/// 8.179-T1 (S2): gestión CRUD de categorías jerárquicas del catálogo.
/// Mutaciones sin permiso → <see cref="UnauthorizedAccessException"/>;
/// recurso inexistente → <see cref="KeyNotFoundException"/>; conflicto (duplicado por padre,
/// ciclos, borrado con hijos/productos) → <see cref="InvalidOperationException"/>.
/// </summary>
public interface ICategoryService
{
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken = default);

    Task<CategoryDto> CreateCategoryAsync(CreateCategoryRequestDto request, CancellationToken cancellationToken = default);

    Task<CategoryDto> UpdateCategoryAsync(int id, UpdateCategoryRequestDto request, CancellationToken cancellationToken = default);

    Task DeleteCategoryAsync(int id, CancellationToken cancellationToken = default);
}
