namespace Core.Entities;

/// <summary>
/// 8.179-T1 (S1): categoría jerárquica del catálogo. Self-FK <see cref="ParentCategoryId"/> con
/// profundidad libre; la unicidad de nombre es por padre (case-insensitive) y se valida en
/// <c>CategoryService</c> porque PostgreSQL trata los NULL como distintos en el índice único.
/// </summary>
public class Category : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>8.179-T1: nombre normalizado (trim + mayúsculas invariantes) para matching e importación.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public int? ParentCategoryId { get; set; }
    public Category? ParentCategory { get; set; }
    public ICollection<Category> Children { get; set; } = new List<Category>();
}
