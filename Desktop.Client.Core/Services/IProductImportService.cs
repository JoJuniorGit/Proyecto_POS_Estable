using System.Collections.Generic;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public interface IProductImportService
{
    Task<string> GenerateTemplateAsync(string destinationPath);
    Task<List<string>> ReadHeadersAsync(string filePath);
    Task<List<Core.DTOs.ProductImportDto>> ParseFileWithMappingAsync(string filePath, Dictionary<string, string> columnMapping, decimal currentExchangeRate);
    Task<(int added, int updated)> CommitImportAsync(IEnumerable<Core.DTOs.ProductImportDto> products, bool overwriteMerge);
    Task<string> ExportProductsToFileAsync(string destinationPath, bool activeOnly = true, string? filter = null);
}
