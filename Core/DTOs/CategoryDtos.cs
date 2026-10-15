using System.ComponentModel.DataAnnotations;

namespace Core.DTOs;

/// <summary>
/// 8.179-T1 (S2): contrato de lectura de una categoría. <see cref="Path"/> es la ruta legible
/// completa ("Alimentos > Lácteos") y <see cref="Depth"/> la cantidad de ancestros (raíz = 0).
/// </summary>
public sealed record CategoryDto(int Id, string Name, int? ParentCategoryId, int Depth, string Path);

/// <summary>8.179-T1 (S3): referencia mínima de categoría expuesta por <see cref="ProductDto"/>.</summary>
public sealed record CategoryRefDto(int Id, string Name);

/// <summary>8.179-T1 (S2): alta de categoría. <c>ParentCategoryId</c> nulo crea una raíz.</summary>
public class CreateCategoryRequestDto
{
    [Required(ErrorMessage = "El nombre de la categoría es obligatorio.")]
    [MaxLength(60, ErrorMessage = "El nombre de la categoría no puede exceder los 60 caracteres.")]
    public string Name { get; set; } = string.Empty;

    public int? ParentCategoryId { get; set; }
}

/// <summary>8.179-T1 (S2): edición de categoría (renombrar y/o mover de padre).</summary>
public class UpdateCategoryRequestDto
{
    [Required(ErrorMessage = "El nombre de la categoría es obligatorio.")]
    [MaxLength(60, ErrorMessage = "El nombre de la categoría no puede exceder los 60 caracteres.")]
    public string Name { get; set; } = string.Empty;

    public int? ParentCategoryId { get; set; }
}
