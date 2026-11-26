using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public enum ImportFilterMode
{
    All,
    Valid,
    Errors
}

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

public partial class ImportProductsViewModel : ObservableObject, IDisposable
{
    private readonly IProductImportService _importService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly IDialogService? _dialogService;
    private readonly IFilePickerDialog _filePicker;
    private readonly ObservableCollection<ProductImportDto> _filteredProducts = new();
    private ObservableCollection<ProductImportDto>? _observedProducts;

    public UserSession UserSession { get; }

    public ImportProductsViewModel(
        IProductImportService importService, 
        IExchangeRateService exchangeRateService, 
        UserSession userSession,
        IDialogService? dialogService = null,
        IFilePickerDialog? filePicker = null)
    {
        _importService = importService;
        _exchangeRateService = exchangeRateService;
        UserSession = userSession;
        _dialogService = dialogService;
        _filePicker = filePicker ?? new NoopFilePicker();
        FilteredProducts = new ReadOnlyObservableCollection<ProductImportDto>(_filteredProducts);
        AttachImportedProducts(ImportedProducts);
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

    [ObservableProperty]
    private ImportFilterMode _selectedFilter = ImportFilterMode.All;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _activeFileName = string.Empty;

    [ObservableProperty]
    private string _activeFilePath = string.Empty;

    [ObservableProperty]
    private ProductImportDto? _selectedProduct;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isImportCompleted;

    public ReadOnlyObservableCollection<ProductImportDto> FilteredProducts { get; }

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
        "Low Stock Threshold",
        "ProductType",
        "GroupNameOrKey",
        "IsStockShared",
        "ConversionFactor",
        "CategoryPath"
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

        if (cleanHeader.Contains("TipoProducto", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("ProductType", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Tipo", StringComparison.OrdinalIgnoreCase))
            return "ProductType";

        if (cleanHeader.Contains("Grupo", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("GroupNameOrKey", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("GrupoPadre", StringComparison.OrdinalIgnoreCase))
            return "GroupNameOrKey";

        if (cleanHeader.Contains("CompartirStock", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("IsStockShared", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("StockCompartido", StringComparison.OrdinalIgnoreCase))
            return "IsStockShared";

        if (cleanHeader.Contains("FactorConversion", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("ConversionFactor", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Equivalencia", StringComparison.OrdinalIgnoreCase))
            return "ConversionFactor";

        // 8.179-T2 (S5-UI): ruta jerárquica separada por '>' (ej. "Alimentos > Lácteos"); el backend
        // auto-crea los segmentos inexistentes. El detector es Contains para cubrir la columna
        // "Categoría"/"Categorías"/"Category" del export y de la plantilla.
        if (cleanHeader.Contains("Categoría", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Categoria", StringComparison.OrdinalIgnoreCase) ||
            cleanHeader.Contains("Category", StringComparison.OrdinalIgnoreCase))
            return "CategoryPath";

        return "Ignore";
    }

    private string _currentFilePath = string.Empty;
    private bool _hasValidProducts;

    public bool HasFileData => ImportedProducts.Any();
    public bool HasMappings => ColumnMappings.Any();
    public bool HasActiveFile => !string.IsNullOrWhiteSpace(ActiveFileName);
    public bool CanCommit => HasFileData && _hasValidProducts && !IsBusy && CanMutateCatalog;

    public int TotalCount => ImportedProducts.Count;
    public int ValidCount => ImportedProducts.Count(p => p.IsValid);
    public int ErrorCount => TotalCount - ValidCount;
    public bool HasErrors => ErrorCount > 0;
    public bool HasValidProducts => ValidCount > 0;
    public bool HasFilteredProducts => _filteredProducts.Count > 0;

    public bool IsFilterAll => SelectedFilter == ImportFilterMode.All;
    public bool IsFilterValid => SelectedFilter == ImportFilterMode.Valid;
    public bool IsFilterErrors => SelectedFilter == ImportFilterMode.Errors;

    public bool IsStep1Active => !HasFileData && !IsImportCompleted;
    public bool IsStep1Complete => HasFileData || IsImportCompleted;
    public bool IsStep2Active => HasFileData && ErrorCount > 0 && !IsImportCompleted;
    public bool IsStep2Complete => IsImportCompleted || (HasFileData && ErrorCount == 0);
    public bool IsStep3Active => HasFileData && ErrorCount == 0 && ValidCount > 0 && !IsImportCompleted;
    public bool IsStep3Complete => IsImportCompleted;

    public bool HasSelectedError => SelectedProduct is { IsValid: false };
    public string SelectedErrorDetail => SelectedProduct?.ErrorMessage ?? string.Empty;
    public string SelectedErrorSku => SelectedProduct?.SKU ?? string.Empty;

    public string AllCountText => $"Todas ({TotalCount})";
    public string ValidCountText => $"Válidas ({ValidCount})";
    public string ErrorCountText => $"Con errores ({ErrorCount})";

    public string ImportStatusText
    {
        get
        {
            if (IsSaving)
            {
                return "Guardando productos en la base de datos...";
            }

            if (IsImportCompleted)
            {
                return "Importación completada correctamente.";
            }

            if (!HasFileData && !HasActiveFile)
            {
                return "Sin archivo cargado.";
            }

            if (!HasFileData)
            {
                return "El archivo no contiene filas para previsualizar.";
            }

            if (HasErrors)
            {
                return $"Se detectaron {ErrorCount} filas con errores; revísalas antes de confirmar.";
            }

            return $"Listo para importar {ValidCount} productos válidos.";
        }
    }

    public string SaveButtonText => IsSaving
        ? "Guardando..."
        : ValidCount > 0
            ? $"Guardar en Base de Datos ({ValidCount})"
            : "Guardar en Base de Datos";

    public string MergeStatusText => OverwriteMerge
        ? "Actualizará productos con SKU coincidente"
        : "Omitir SKUs existentes";

    public string MergeTooltip => OverwriteMerge
        ? "Actualiza los productos existentes que coincidan por SKU."
        : "Omite los productos cuyo SKU ya exista en la base de datos.";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCommit));
        CommitImportCommand?.NotifyCanExecuteChanged();
    }

    partial void OnSelectedFilterChanged(ImportFilterMode value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterValid));
        OnPropertyChanged(nameof(IsFilterErrors));
        ApplyFilters();
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilters();

    partial void OnActiveFileNameChanged(string value) => OnPropertyChanged(nameof(HasActiveFile));

    partial void OnSelectedProductChanged(ProductImportDto? value)
    {
        OnPropertyChanged(nameof(HasSelectedError));
        OnPropertyChanged(nameof(SelectedErrorDetail));
        OnPropertyChanged(nameof(SelectedErrorSku));
    }

    partial void OnOverwriteMergeChanged(bool value)
    {
        OnPropertyChanged(nameof(MergeStatusText));
        OnPropertyChanged(nameof(MergeTooltip));
    }

    partial void OnIsSavingChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(ImportStatusText));
    }

    partial void OnIsImportCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(ImportStatusText));
        NotifyStepperState();
    }

    private void RefreshValidationCache()
    {
        _hasValidProducts = ImportedProducts.Any(p => p.IsValid);
        OnPropertyChanged(nameof(HasFileData));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ValidCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasValidProducts));
        OnPropertyChanged(nameof(AllCountText));
        OnPropertyChanged(nameof(ValidCountText));
        OnPropertyChanged(nameof(ErrorCountText));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(ImportStatusText));
        OnPropertyChanged(nameof(SaveButtonText));
        NotifyStepperState();
        CommitImportCommand?.NotifyCanExecuteChanged();
        ApplyFilters();
    }

    private void NotifyStepperState()
    {
        OnPropertyChanged(nameof(IsStep1Active));
        OnPropertyChanged(nameof(IsStep1Complete));
        OnPropertyChanged(nameof(IsStep2Active));
        OnPropertyChanged(nameof(IsStep2Complete));
        OnPropertyChanged(nameof(IsStep3Active));
        OnPropertyChanged(nameof(IsStep3Complete));
    }

    private void ApplyFilters()
    {
        var term = SearchQuery?.Trim() ?? string.Empty;
        _filteredProducts.Clear();

        foreach (var product in ImportedProducts)
        {
            if (SelectedFilter == ImportFilterMode.Valid && !product.IsValid)
            {
                continue;
            }

            if (SelectedFilter == ImportFilterMode.Errors && product.IsValid)
            {
                continue;
            }

            if (term.Length > 0 &&
                !product.SKU.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !product.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _filteredProducts.Add(product);
        }

        if (SelectedProduct is not null && !_filteredProducts.Contains(SelectedProduct))
        {
            SelectedProduct = null;
        }

        OnPropertyChanged(nameof(HasFilteredProducts));
    }

    [RelayCommand]
    private void SetFilterAll() => SelectedFilter = ImportFilterMode.All;

    [RelayCommand]
    private void SetFilterValid() => SelectedFilter = ImportFilterMode.Valid;

    [RelayCommand]
    private void SetFilterErrors() => SelectedFilter = ImportFilterMode.Errors;

    partial void OnImportedProductsChanged(ObservableCollection<ProductImportDto> value)
    {
        AttachImportedProducts(value);
        OnPropertyChanged(nameof(HasFileData));
        RefreshValidationCache();
    }

    private void AttachImportedProducts(ObservableCollection<ProductImportDto> products)
    {
        if (ReferenceEquals(_observedProducts, products))
        {
            return;
        }

        if (_observedProducts is not null)
        {
            _observedProducts.CollectionChanged -= OnImportedProductsCollectionChanged;
        }

        _observedProducts = products;
        _observedProducts.CollectionChanged += OnImportedProductsCollectionChanged;
    }

    private void OnImportedProductsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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

        var saveFilePath = _filePicker.PickSaveFilePath(
            "Exportar Catálogo de Productos",
            "Archivo Excel (*.xlsx)|*.xlsx|Archivo CSV (*.csv)|*.csv",
            $"Productos_Catalogo_{DateTime.Now:yyyyMMdd}.xlsx");

        if (!string.IsNullOrEmpty(saveFilePath))
        {
            IsBusy = true;
            IsProgressIndeterminate = true;
            StatusMessage = "Generando reporte de productos...";

            try
            {
                await _importService.ExportProductsToFileAsync(saveFilePath, ExportActiveOnly);
                StatusMessage = "Catálogo exportado exitosamente.";
                ShowInfo("Exportación Exitosa", $"El catálogo de productos se ha exportado correctamente en:\n{saveFilePath}");
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

        var templatePath = _filePicker.PickSaveFilePath(
            "Guardar Plantilla de Importación",
            "Archivo Excel (*.xlsx)|*.xlsx|Archivo CSV (*.csv)|*.csv",
            "Productos_Plantilla_Importacion.xlsx");

        if (!string.IsNullOrEmpty(templatePath))
        {
            IsBusy = true;
            IsProgressIndeterminate = true;
            StatusMessage = "Creando plantilla de importación...";
            try
            {
                await _importService.GenerateTemplateAsync(templatePath);
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

        var filePath = _filePicker.PickFilePath(
            "Seleccionar Archivo de Productos",
            "Archivos compatibles (*.xlsx;*.csv)|*.xlsx;*.csv|Excel Files (*.xlsx)|*.xlsx|CSV Files (*.csv)|*.csv");

        if (!string.IsNullOrEmpty(filePath))
        {
            await ProcessFileAsync(filePath);
        }
    }

    public async Task ProcessFileAsync(string filePath)
    {
        IsImportCompleted = false;
        ImportedProducts.Clear();
        ColumnMappings.Clear();
        OnPropertyChanged(nameof(HasMappings));
        SelectedProduct = null;
        SelectedFilter = ImportFilterMode.All;
        SearchQuery = string.Empty;
        ActiveFileName = Path.GetFileName(filePath);
        ActiveFilePath = filePath;
        _currentFilePath = filePath;
        RefreshValidationCache();

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Leyendo encabezados del archivo...";
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

            OnPropertyChanged(nameof(HasMappings));
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

        IsImportCompleted = false;
        SelectedProduct = null;
        SelectedFilter = ImportFilterMode.All;
        SearchQuery = string.Empty;
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

        IsSaving = true;
        IsImportCompleted = false;
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
                IsImportCompleted = true;
                StatusMessage = $"Importación exitosa: {added} agregados, {updated} actualizados.";
                ShowInfo("Éxito de Importación", StatusMessage);
            }
            else
            {
                IsImportCompleted = false;
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
            IsSaving = false;
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void ClearData()
    {
        ImportedProducts.Clear();
        ColumnMappings.Clear();
        OnPropertyChanged(nameof(HasMappings));
        _currentFilePath = string.Empty;
        ActiveFileName = string.Empty;
        ActiveFilePath = string.Empty;
        SelectedProduct = null;
        SelectedFilter = ImportFilterMode.All;
        SearchQuery = string.Empty;
        IsImportCompleted = false;
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

    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
            if (_observedProducts is not null)
            {
                _observedProducts.CollectionChanged -= OnImportedProductsCollectionChanged;
            }

            ImportedProducts.Clear();
            ColumnMappings.Clear();
        }
        _disposed = true;
    }
}
