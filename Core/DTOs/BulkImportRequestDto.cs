namespace Core.DTOs;

public class BulkImportRequestDto
{
    public System.Collections.Generic.List<ProductImportDto> Products { get; set; } = new();
    public bool OverwriteMerge { get; set; }
}

public class BulkImportResponseDto
{
    public int Added { get; set; }
    public int Updated { get; set; }
}
