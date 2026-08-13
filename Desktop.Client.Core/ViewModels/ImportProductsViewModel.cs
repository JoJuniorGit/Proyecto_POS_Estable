using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

public partial class ColumnMappingItem : ObservableObject
{
    public string ExcelHeader { get; }
    public ObservableCollection<string> AvailableProperties { get; }
    
    [ObservableProperty]
    private string _selectedProperty;

    public ColumnMappingItem(string excelHeader, ObservableCollection<string> availableProperties)
    {
        ExcelHeader = excelHeader;
        AvailableProperties = availableProperties;
        _selectedProperty = "Ignore";
    }
}

public partial class ImportProductsViewModel : ObservableObject
{
    private readonly IProductImportService _importService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly IDialogService? _dialogService;

    public UserSession UserSession { get; }

    public ImportProductsViewModel(
        IProductImportService importService, 
        IExchangeRateService exchangeRateService, 
        UserSession userSession,
        IDialogService? dialogService = null)
    {
        _importService = importService;
        _exchangeRateService = exchangeRateService;
        UserSession = userSession;
        _dialogService = dialogService;
    }

    public decimal CurrentExchangeRate => _exchangeRateService.CurrentRate;
    public bool CanMutateCatalog => UserSession.CanMutateCatalog;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Seleccione un archivo Excel/CSV para comenzar o exporte el catálogo.";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private bool _overwriteMerge = false;

    [ObservableProperty]
    private bool _exportActiveOnly = true;

    [ObservableProperty]
    private ObservableCollection<ProductImportDto> _importedProducts = new();

    [ObservableProperty]
    private ObservableCollection<ColumnMappingItem> _columnMappings = new();

    public ObservableCollection<string> AvailableProperties { get; } = new ObservableCollection<string>
    {
        "Ignore",
        "SKU",
        "Name",
        "Description",
        "CostPriceUSD",
        "ProfitMarginRetail",
        "ProfitMarginWholesale",
        "MinWholesaleQuantity",
        "HasWholesale",
        "IsFractional",
        "StockQuantity",
        "LowStockThreshold",
        "Cost (USD)",
        "Retail Margin (%)",
        "Wholesale Margin (%)",
        "Min Wholesale Quantity",
        "Enable Wholesale",
        "Is Fractional",
        "Current Stock",
        "Low Stock Threshold"
    };

    public static string MapHeaderToProperty(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return "Ignore";
        var cleanHeader = header.Trim();

        if (string.Equals(cleanHeader, "SKU", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Codigo", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Código", StringComparison.OrdinalIgnoreCase))
            return "SKU";

        if (string.Equals(cleanHeader, "Nombre", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cleanHeader, "Name", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cleanHeader, "Producto", StringComparison.OrdinalIgnoreCase))
            return "Name";

        if (cleanHeader.Contains("Descripción", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Descripcion", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Description", StringComparison.OrdinalIgnoreCase))
            return "Description";

        if (cleanHeader.Contains("Costo", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("CostPriceUSD", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Cost", StringComparison.OrdinalIgnoreCase))
            return "CostPriceUSD";

        if (cleanHeader.Contains("MargenDetal", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("ProfitMarginRetail", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Retail Margin", StringComparison.OrdinalIgnoreCase))
            return "ProfitMarginRetail";

        if (cleanHeader.Contains("MargenMayor", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("ProfitMarginWholesale", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Wholesale Margin", StringComparison.OrdinalIgnoreCase))
            return "ProfitMarginWholesale";

        if (cleanHeader.Contains("CantMinMayor", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("MinWholesaleQuantity", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Min Wholesale", StringComparison.OrdinalIgnoreCase))
            return "MinWholesaleQuantity";

        if (cleanHeader.Contains("HabilitarMayor", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("HasWholesale", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Enable Wholesale", StringComparison.OrdinalIgnoreCase))
            return "HasWholesale";

        if (cleanHeader.Contains("EsFraccionable", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("IsFractional", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Is Fractional", StringComparison.OrdinalIgnoreCase))
            return "IsFractional";

        if (cleanHeader.Contains("StockActual", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("StockQuantity", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Current Stock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cleanHeader, "Stock", StringComparison.OrdinalIgnoreCase))
            return "StockQuantity";

        if (cleanHeader.Contains("UmbralMinimo", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("LowStockThreshold", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Low Stock", StringComparison.OrdinalIgnoreCase))
            return "LowStockThreshold";

        return "Ignore";
    }

    private string _currentFilePath = string.Empty;
    private bool _hasValidProducts;

    public bool HasFileData => ImportedProducts.Any();
    public bool HasMappings => ColumnMappings.Any();
    public bool CanCommit => HasFileData && _hasValidProducts && !IsBusy && CanMutateCatalog;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCommit));
        CommitImportCommand?.NotifyCanExecuteChanged();
    }

    private void RefreshValidationCache()
    {
        _hasValidProducts = ImportedProducts.Any(p => p.IsValid);
        OnPropertyChanged(nameof(CanCommit));
        CommitImportCommand?.NotifyCanExecuteChanged();
    }

    partial void OnImportedProductsChanged(ObservableCollection<ProductImportDto> value)
    {
        OnPropertyChanged(nameof(HasFileData));
        RefreshValidationCache();
    }
    
    partial void OnColumnMappingsChanged(ObservableCollection<ColumnMappingItem> value)
    {
        OnPropertyChanged(nameof(HasMappings));
    }

    [RelayCommand]
    private async Task ExportCatalogAsync()
    {
        if (!CanMutateCatalog)
        {
            ShowWarning("Acceso Denegado", "El rol Cajero no tiene permisos para realizar exportaciones.");
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Filter = "Archivo Excel (*.xlsx)|*.xlsx|Archivo CSV (*.csv)|*.csv",
            Title = "Exportar Catálogo de Productos",
            FileName = $"Productos_Catalogo_{DateTime.Now:yyyyMMdd}.xlsx"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            IsBusy = true;
            IsProgressIndeterminate = true;
            StatusMessage = "Generando reporte de productos...";

            try
            {
                await _importService.ExportProductsToFileAsync(saveFileDialog.FileName, ExportActiveOnly);
                StatusMessage = "Catálogo exportado exitosamente.";
                ShowInfo("Exportación Exitosa", $"El catálogo de productos se ha exportado correctamente en:\n{saveFileDialog.FileName}");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error al exportar catálogo: {ex.Message}";
                ShowError("Error de Exportación", $"No se pudo exportar el catálogo:\n\n{ex.Message}");
            }
            finally
            {
                IsBusy = false;
                IsProgressIndeterminate = false;
            }
        }
    }

    [RelayCommand]
    private async Task DownloadTemplateAsync()
    {
        if (!CanMutateCatalog)
        {
            ShowWarning("Acceso Denegado", "El rol Cajero no tiene permisos para descargar la plantilla.");
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Filter = "Archivo Excel (*.xlsx)|*.xlsx|Archivo CSV (*.csv)|*.csv",
            Title = "Guardar Plantilla de Importación",
            FileName = "Productos_Plantilla_Importacion.xlsx"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            IsBusy = true;
            IsProgressIndeterminate = true;
            StatusMessage = "Creando plantilla de importación...";
            try
            {
                await _importService.GenerateTemplateAsync(saveFileDialog.FileName);
                StatusMessage = "Plantilla guardada exitosamente.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error creando plantilla: {ex.Message}";
                ShowError("Error de Plantilla", $"No se pudo generar la plantilla:\n\n{ex.Message}");
            }
            finally
            {
                IsBusy = false;
                IsProgressIndeterminate = false;
                ProgressValue = 0;
            }
        }
    }

    [RelayCommand]
    private async Task SelectFileAsync()
    {
        if (!CanMutateCatalog)
        {
            ShowWarning("Acceso Denegado", "El rol Cajero no tiene permisos para realizar importaciones.");
            return;
        }

        var openFileDialog = new OpenFileDialog
        {
            Filter = "Archivos compatibles (*.xlsx;*.csv)|*.xlsx;*.csv|Excel Files (*.xlsx)|*.xlsx|CSV Files (*.csv)|*.csv",
            Title = "Seleccionar Archivo de Productos"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            await ProcessFileAsync(openFileDialog.FileName);
        }
    }

    public async Task ProcessFileAsync(string filePath)
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Leyendo encabezados del archivo...";
        _currentFilePath = filePath;
        bool readSuccess = false;
        
        try
        {
            var headers = await _importService.ReadHeadersAsync(filePath);

            if (headers == null || !headers.Any())
            {
                StatusMessage = "No se encontraron encabezados en el archivo seleccionado.";
                ShowWarning("Archivo Vacío", "El archivo seleccionado está vacío o no contiene encabezados válidos.");
                return;
            }
            
            ColumnMappings.Clear();
            foreach (var header in headers)
            {
                var mapping = new ColumnMappingItem(header, AvailableProperties);
                
                var matchedProp = MapHeaderToProperty(header);
                if (matchedProp != "Ignore" && AvailableProperties.Contains(matchedProp))
                {
                    mapping.SelectedProperty = matchedProp;
                }
                else
                {
                    var fallback = AvailableProperties.FirstOrDefault(p => p != "Ignore" && header.Contains(p, StringComparison.OrdinalIgnoreCase));
                    if (fallback != null)
                    {
                        mapping.SelectedProperty = fallback;
                    }
                }
                
                ColumnMappings.Add(mapping);
            }
            
            StatusMessage = "Encabezados leídos. Procesando filas...";
            readSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al leer encabezados: {ex.Message}";
            ShowError("Error de Lectura", $"No se pudo leer el archivo:\n\n{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }

        if (readSuccess && ColumnMappings.Any())
        {
            await ProcessDataAsync();
        }
    }

    [RelayCommand]
    private async Task ProcessDataAsync()
    {
        if (string.IsNullOrEmpty(_currentFilePath) || !ColumnMappings.Any()) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Procesando y aplicando validaciones de negocio...";

        try
        {
            var mappingDict = ColumnMappings
                .GroupBy(m => m.ExcelHeader)
                .ToDictionary(g => g.Key, g => g.First().SelectedProperty);

            var parsedList = await _importService.ParseFileWithMappingAsync(_currentFilePath, mappingDict, CurrentExchangeRate);
            
            ImportedProducts = new ObservableCollection<ProductImportDto>(parsedList);
            
            int total = parsedList.Count;
            int validCount = parsedList.Count(p => p.IsValid);

            if (total == 0)
            {
                StatusMessage = "El archivo está vacío o no contiene filas con datos válidos.";
                ShowInfo("Sin Datos", "El archivo no contiene filas con información de productos.");
            }
            else if (validCount == total)
            {
                StatusMessage = $"Se cargaron exitosamente {total} filas válidas listas para importar.";
            }
            else
            {
                StatusMessage = $"Se cargaron {total} filas. {(total - validCount)} contienen errores de validación. Corrija las filas resaltadas en rojo.";
                ShowWarning("Atención en Importación", $"Se leyeron {total} filas, pero {(total - validCount)} presentan errores de validación (resaltados en rojo). Por favor revise la tabla antes de guardar.");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al procesar archivo: {ex.Message}";
            ImportedProducts.Clear();
            ShowError("Error de Procesamiento", $"Error al procesar los datos del archivo:\n\n{ex.Message}");
        }
        finally
        {
            RefreshValidationCache();
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitImportAsync()
    {
        if (!CanCommit) return;

        var validProducts = ImportedProducts.Where(p => p.IsValid).ToList();
        if (!validProducts.Any()) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Guardando productos en la base de datos PostgreSQL...";

        try
        {
            var (added, updated) = await _importService.CommitImportAsync(validProducts, OverwriteMerge);
            
            // Broadcast catalog update message to update InventoryView reactively
            WeakReferenceMessenger.Default.Send(new CatalogUpdatedMessage());

            // Automatically remove successfully imported valid rows from preview
            foreach (var p in validProducts)
            {
                ImportedProducts.Remove(p);
            }

            if (!ImportedProducts.Any())
            {
                StatusMessage = $"Importación exitosa: {added} agregados, {updated} actualizados.";
                ShowInfo("Éxito de Importación", StatusMessage);
            }
            else
            {
                int remainingErrors = ImportedProducts.Count;
                StatusMessage = $"Importación parcial: {added} agregados, {updated} actualizados. Quedan {remainingErrors} filas con errores en pantalla para su revisión.";
                ShowWarning("Importación Parcial Completada", $"Se importaron exitosamente {validProducts.Count} productos ({added} agregados, {updated} actualizados).\n\nLas {remainingErrors} filas que contienen errores permanecen en la tabla para su revisión o corrección.");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al guardar en base de datos: {ex.Message}";
            ShowError("Error de Importación", StatusMessage);
        }
        finally
        {
            RefreshValidationCache();
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void ClearData()
    {
        ImportedProducts.Clear();
        ColumnMappings.Clear();
        _currentFilePath = string.Empty;
        StatusMessage = "Seleccione un archivo Excel/CSV para comenzar o exporte el catálogo.";
        RefreshValidationCache();
    }

    private void ShowError(string title, string message)
    {
        _dialogService?.ShowError(title, message);
    }

    private void ShowWarning(string title, string message)
    {
        _dialogService?.ShowWarning(title, message);
    }

    private void ShowInfo(string title, string message)
    {
        _dialogService?.ShowInfo(title, message);
    }
}
