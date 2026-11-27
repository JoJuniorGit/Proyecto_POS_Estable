using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>
/// F4-WU1 (Paso 14): exportación CSV/XLSX/PDF contra el endpoint server-side F3. El archivo se
/// escribe en la ruta elegida por el selector nativo; cancelar no es un error.
/// </summary>
public partial class ProfitabilityViewModel
{
    [RelayCommand]
    private Task ExportCsvAsync() => ExportFileAsync("csv", "CSV (*.csv)|*.csv");

    [RelayCommand]
    private Task ExportXlsxAsync() => ExportFileAsync("xlsx", "Excel (*.xlsx)|*.xlsx");

    [RelayCommand]
    private Task ExportPdfAsync() => ExportFileAsync("pdf", "PDF (*.pdf)|*.pdf");

    private async Task ExportFileAsync(string format, string pickerFilter)
    {
        if (!CanExport)
        {
            return;
        }

        IsExporting = true;
        ExportStatus = string.Empty;
        try
        {
            var file = await _profitabilityService.ExportAsync(BuildFilter(), format);
            var path = _filePicker.PickSaveFilePath(
                "Exportar Rentabilidad",
                pickerFilter,
                file.FileName);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await File.WriteAllBytesAsync(path, file.Content);
            ExportStatus = $"Reporte exportado correctamente en {path}.";
            _dialogService?.ShowInfo(
                "Exportación completada",
                $"El Estado de Resultados se guardó correctamente en:\n{path}");
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogError(
                $"Error al exportar la rentabilidad ({format}): {ex.Message}",
                nameof(ProfitabilityViewModel));
            ExportStatus = $"No se pudo exportar el reporte: {ex.Message}";
            _dialogService?.ShowError(
                "Error de exportación",
                $"No se pudo exportar el Estado de Resultados: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }
}
