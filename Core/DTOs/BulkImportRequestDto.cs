namespace Core.DTOs;

public class BulkImportRequestDto
{
    public System.Collections.Generic.List<ProductImportDto> Products { get; set; } = new();
    public bool OverwriteMerge { get; set; }
}
