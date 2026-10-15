using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

/// <summary>
/// 8.179-T1 (S2): CRUD validado de categorías jerárquicas. Reglas:
/// nombre trim/no vacío/≤60; unicidad por padre case-insensitive (incluye raíces, donde el índice
/// único de PostgreSQL no alcanza porque NULL es distinto); sin ciclos al mover; borrado bloqueado
/// con hijas o productos asignados. Los conflictos se lanzan como <see cref="InvalidOperationException"/>
/// (el middleware global los mapea a 409 RFC 7807) y la ausencia como <see cref="KeyNotFoundException"/> (404).
/// </summary>
public class CategoryService : ICategoryService
{
    private readonly InventoryDbContext _context;

    public CategoryService(InventoryDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _context.Categories.AsNoTracking().ToListAsync(cancellationToken);
        var byId = categories.ToDictionary(c => c.Id);

        return categories
            .Select(c => ToDto(c, byId))
            .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Id)
            .ToList();
    }

    public async Task<CategoryDto> CreateCategoryAsync(CreateCategoryRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (displayName, normalizedName) = NormalizeName(request.Name);

        if (request.ParentCategoryId.HasValue)
        {
            await EnsureCategoryExistsAsync(request.ParentCategoryId.Value, cancellationToken);
        }

        await EnsureNameIsUniqueAsync(request.ParentCategoryId, normalizedName, displayName, excludingId: null, cancellationToken);

        var category = new Category
        {
            Name = displayName,
            NormalizedName = normalizedName,
            ParentCategoryId = request.ParentCategoryId
        };

        _context.Categories.Add(category);
        await _context.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(category, cancellationToken);
    }

    public async Task<CategoryDto> UpdateCategoryAsync(int id, UpdateCategoryRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var category = await _context.Categories.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new KeyNotFoundException($"Categoría con ID {id} no encontrada.");

        var (displayName, normalizedName) = NormalizeName(request.Name);

        if (request.ParentCategoryId.HasValue)
        {
            await EnsureMoveIsAcyclicAsync(id, request.ParentCategoryId.Value, cancellationToken);
        }

        await EnsureNameIsUniqueAsync(request.ParentCategoryId, normalizedName, displayName, excludingId: id, cancellationToken);

        category.Name = displayName;
        category.NormalizedName = normalizedName;
        category.ParentCategoryId = request.ParentCategoryId;
        category.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return await BuildDtoAsync(category, cancellationToken);
    }

    public async Task DeleteCategoryAsync(int id, CancellationToken cancellationToken = default)
    {
        var category = await _context.Categories.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new KeyNotFoundException($"Categoría con ID {id} no encontrada.");

        if (await _context.Categories.AnyAsync(c => c.ParentCategoryId == id, cancellationToken))
        {
            throw new InvalidOperationException($"La categoría '{category.Name}' tiene subcategorías y no puede eliminarse.");
        }

        if (await _context.ProductCategories.AnyAsync(pc => pc.CategoryId == id, cancellationToken))
        {
            throw new InvalidOperationException($"La categoría '{category.Name}' tiene productos asignados y no puede eliminarse.");
        }

        _context.Categories.Remove(category);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static (string Display, string Normalized) NormalizeName(string? name)
    {
        var display = (name ?? string.Empty).Trim();
        if (display.Length == 0)
        {
            throw new ArgumentException("El nombre de la categoría no puede estar vacío.", nameof(name));
        }
        if (display.Length > 60)
        {
            throw new ArgumentException("El nombre de la categoría no puede exceder los 60 caracteres.", nameof(name));
        }

        return (display, display.ToUpperInvariant());
    }

    private async Task EnsureCategoryExistsAsync(int parentId, CancellationToken cancellationToken)
    {
        if (!await _context.Categories.AnyAsync(c => c.Id == parentId, cancellationToken))
        {
            throw new KeyNotFoundException($"Categoría padre con ID {parentId} no encontrada.");
        }
    }

    private async Task EnsureNameIsUniqueAsync(int? parentId, string normalizedName, string displayName, int? excludingId, CancellationToken cancellationToken)
    {
        bool duplicate = await _context.Categories.AnyAsync(
            c => c.ParentCategoryId == parentId
                 && c.NormalizedName == normalizedName
                 && (excludingId == null || c.Id != excludingId.Value),
            cancellationToken);

        if (duplicate)
        {
            throw new InvalidOperationException($"Ya existe una categoría con el nombre '{displayName}' en este nivel.");
        }
    }

    /// <summary>
    /// 8.179-T1: la categoría destino no puede ser la propia categoría ni una descendiente. Se recorre
    /// la cadena de ancestros hacia arriba desde el nuevo padre con guard de ciclos preexistentes.
    /// </summary>
    private async Task EnsureMoveIsAcyclicAsync(int categoryId, int newParentId, CancellationToken cancellationToken)
    {
        if (newParentId == categoryId)
        {
            throw new InvalidOperationException("Una categoría no puede ser su propia categoría padre.");
        }

        var parentById = await _context.Categories.AsNoTracking()
            .Select(c => new { c.Id, c.ParentCategoryId })
            .ToDictionaryAsync(c => c.Id, c => c.ParentCategoryId, cancellationToken);

        if (!parentById.ContainsKey(newParentId))
        {
            throw new KeyNotFoundException($"Categoría padre con ID {newParentId} no encontrada.");
        }

        var visited = new HashSet<int>();
        int? current = newParentId;
        while (current.HasValue)
        {
            if (current.Value == categoryId)
            {
                throw new InvalidOperationException("No se puede mover la categoría bajo sí misma ni bajo una de sus subcategorías.");
            }
            if (!visited.Add(current.Value))
            {
                break; // Cadena de ancestros corrupta (ciclo preexistente): cortar para no colgar.
            }

            current = parentById.TryGetValue(current.Value, out var parentId) ? parentId : null;
        }
    }

    private async Task<CategoryDto> BuildDtoAsync(Category category, CancellationToken cancellationToken)
    {
        var categories = await _context.Categories.AsNoTracking().ToListAsync(cancellationToken);
        var byId = categories.ToDictionary(c => c.Id);
        return ToDto(category, byId);
    }

    private static CategoryDto ToDto(Category category, IReadOnlyDictionary<int, Category> byId)
    {
        var (depth, path) = CategoryHierarchy.Describe(category, byId);
        return new CategoryDto(category.Id, category.Name, category.ParentCategoryId, depth, path);
    }
}

/// <summary>
/// 8.179-T1 (T1): utilidades puras de jerarquía compartidas por el CRUD de categorías, el import
/// con auto-creación de rutas y el export de la columna "Categorías".
/// </summary>
internal static class CategoryHierarchy
{
    /// <summary>Profundidad (cantidad de ancestros) y ruta legible "A > B > C" de una categoría.</summary>
    public static (int Depth, string Path) Describe(Category category, IReadOnlyDictionary<int, Category> byId)
    {
        var separator = " > ";
        var segments = new List<string> { category.Name };
        var visited = new HashSet<int> { category.Id };
        var current = category;
        int depth = 0;

        while (current.ParentCategoryId is int parentId && byId.TryGetValue(parentId, out var parent))
        {
            if (!visited.Add(parent.Id))
            {
                break; // Datos corruptos: cortar en vez de colgar.
            }
            segments.Add(parent.Name);
            current = parent;
            depth++;
        }

        segments.Reverse();
        return (depth, string.Join(separator, segments));
    }

    /// <summary>
    /// Ruta por categoría para todas las categorías dadas. Con <paramref name="normalized"/> usa
    /// <c>NormalizedName</c> y separador compacto ">" (matching del import); si no, la ruta legible.
    /// </summary>
    public static Dictionary<int, string> BuildPaths(IReadOnlyList<Category> categories, bool normalized = false)
    {
        var byId = categories.ToDictionary(c => c.Id);
        var paths = new Dictionary<int, string>(categories.Count);

        foreach (var category in categories)
        {
            var segments = new List<string> { normalized ? category.NormalizedName : category.Name };
            var visited = new HashSet<int> { category.Id };
            var current = category;

            while (current.ParentCategoryId is int parentId && byId.TryGetValue(parentId, out var parent))
            {
                if (!visited.Add(parent.Id))
                {
                    break; // Datos corruptos: cortar en vez de colgar.
                }
                segments.Add(normalized ? parent.NormalizedName : parent.Name);
                current = parent;
            }

            segments.Reverse();
            paths[category.Id] = string.Join(normalized ? ">" : " > ", segments);
        }

        return paths;
    }
}
