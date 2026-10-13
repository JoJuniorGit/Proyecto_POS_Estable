using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Core.Interfaces;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClientSupplierInvoiceService = Desktop.Client.Services.ISupplierInvoiceService;

namespace Desktop.Client.ViewModels;

public sealed partial class OcrCharacterReplacementRuleItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _from = string.Empty;

    [ObservableProperty]
    private string _to = string.Empty;

    public OcrCharacterReplacementRuleItemViewModel()
    {
    }

    public OcrCharacterReplacementRuleItemViewModel(string from, string to)
    {
        _from = from ?? string.Empty;
        _to = to ?? string.Empty;
    }

    public OcrCharacterReplacementRuleDto ToDto() => new(From, To);
}

public sealed record OcrColumnPositionOption(int? Position, string Label)
{
    public override string ToString() => Label;
}

public sealed record OcrFactorPatternOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// ViewModel del panel interactivo "No-Code" de configuración avanzada del escáner OCR y plantillas por proveedor.
/// </summary>
public sealed partial class OcrScannerConfigDialogViewModel : ObservableObject
{
    private readonly ClientSupplierInvoiceService _invoiceService;
    private readonly IFilePickerDialog _filePicker;

    public int SupplierId { get; }
    public string SupplierName { get; }
    public SupplierColumnMappingDto? ColumnMapping { get; }

    public IReadOnlyList<OcrColumnPositionOption> ColumnPositionOptions { get; } =
    [
        new OcrColumnPositionOption(null, "Automático (por encabezado)"),
        new OcrColumnPositionOption(1, "Columna 1 (izquierda)"),
        new OcrColumnPositionOption(2, "Columna 2"),
        new OcrColumnPositionOption(3, "Columna 3"),
        new OcrColumnPositionOption(4, "Columna 4"),
        new OcrColumnPositionOption(5, "Columna 5"),
        new OcrColumnPositionOption(6, "Columna 6")
    ];

    public IReadOnlyList<OcrFactorPatternOption> FactorPatternOptions { get; } =
    [
        new OcrFactorPatternOption("NONE", "Sin extracción automática de factor"),
        new OcrFactorPatternOption("AUTO", "Automático (todos los formatos comunes)"),
        new OcrFactorPatternOption("1X40", "Formato multiplicador (ej. 1X40, 36X250GR)"),
        new OcrFactorPatternOption("BTO_X_N", "Formato bulto/display (ej. BTO X 24, DISP 12)"),
        new OcrFactorPatternOption("CAJA_N_UND", "Formato caja/paquete (ej. CAJA 12 UND, PACK 6)"),
        new OcrFactorPatternOption("CUSTOM", "Expresión regular personalizada")
    ];

    public ObservableCollection<OcrCharacterReplacementRuleItemViewModel> CharacterReplacements { get; } = new();
    public ObservableCollection<OcrExtractedLineDto> SandboxLines { get; } = new();

    // Bloque 1: Zona de Lectura (ROI)
    [ObservableProperty]
    private double _roiX;

    [ObservableProperty]
    private double _roiY;

    [ObservableProperty]
    private double _roiWidth = 1.0;

    [ObservableProperty]
    private double _roiHeight = 1.0;

    // Bloque 2: Preprocesamiento Visual
    [ObservableProperty]
    private double _rotationDegrees;

    [ObservableProperty]
    private double _contrastFactor = 1.0;

    [ObservableProperty]
    private double _brightnessDelta;

    [ObservableProperty]
    private bool _useCustomBinarizationThreshold;

    [ObservableProperty]
    private int _binarizationThreshold = 128;

    [ObservableProperty]
    private bool _showPreprocessedPreview;

    // Bloque 3: Anclas de Inicio y Fin de Tabla
    [ObservableProperty]
    private string _headerAnchorKeywords = string.Empty;

    [ObservableProperty]
    private string _footerAnchorKeywords = string.Empty;

    // Bloque 4: Orden y Mapeo de Columnas
    [ObservableProperty]
    private int? _productColumnPosition;

    [ObservableProperty]
    private int? _quantityColumnPosition;

    [ObservableProperty]
    private int? _unitCostColumnPosition;

    [ObservableProperty]
    private int? _supplierCodeColumnPosition;

    // Bloque 5: Reglas Numéricas y Matemáticas
    [ObservableProperty]
    private OcrDecimalSeparatorRule _decimalSeparator = OcrDecimalSeparatorRule.Auto;

    [ObservableProperty]
    private OcrCostAmountRule _costAmountRule = OcrCostAmountRule.UnitCost;

    // Bloque 6: Limpieza de Texto y Factor de Empaque
    [ObservableProperty]
    private string _newReplacementFrom = string.Empty;

    [ObservableProperty]
    private string _newReplacementTo = string.Empty;

    [ObservableProperty]
    private string _selectedFactorPatternKey = "NONE";

    [ObservableProperty]
    private string _customFactorRegex = string.Empty;

    // Estado del Sandbox / Vista previa
    [ObservableProperty]
    private string _sampleFilePath = string.Empty;

    [ObservableProperty]
    private byte[]? _originalSampleBytes;

    [ObservableProperty]
    private byte[]? _preprocessedPreviewBytes;

    [ObservableProperty]
    private string _rawExtractedText = string.Empty;

    [ObservableProperty]
    private string? _detectedRif;

    [ObservableProperty]
    private string? _detectedInvoiceNumber;

    [ObservableProperty]
    private decimal? _detectedExchangeRate;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Cargue una imagen o PDF de muestra para probar los ajustes en vivo, o guarde la plantilla del proveedor.";

    public SupplierOcrTemplateDto? SavedTemplate { get; private set; }
    public OcrExtractionResultDto? LastExtractionResult { get; private set; }
    public bool ApplyResultToStaging { get; private set; }

    public bool HasSampleFile => !string.IsNullOrWhiteSpace(SampleFilePath);
    public bool HasPreprocessedPreview => PreprocessedPreviewBytes is { Length: > 0 };
    public bool HasCustomRoi => RoiX > 0.001 || RoiY > 0.001 || RoiWidth < 0.999 || RoiHeight < 0.999;
    public bool IsCustomFactorPattern => string.Equals(SelectedFactorPatternKey, "CUSTOM", StringComparison.OrdinalIgnoreCase);
    public bool CanTestExtraction => !IsBusy && HasSampleFile;
    public bool CanSaveTemplate => !IsBusy;
    public bool CanApplyToInvoice => !IsBusy && LastExtractionResult is { Lines.Count: > 0 };

    public string RoiSummaryText => HasCustomRoi
        ? $"X: {RoiX:P0} | Y: {RoiY:P0} | Ancho: {RoiWidth:P0} | Alto: {RoiHeight:P0}"
        : "Página completa (100% × 100%) — arrastre sobre la imagen para delimitar la tabla";

    public bool IsDecimalAuto
    {
        get => DecimalSeparator == OcrDecimalSeparatorRule.Auto;
        set
        {
            if (value)
            {
                DecimalSeparator = OcrDecimalSeparatorRule.Auto;
            }
        }
    }

    public bool IsDecimalComma
    {
        get => DecimalSeparator == OcrDecimalSeparatorRule.Comma;
        set
        {
            if (value)
            {
                DecimalSeparator = OcrDecimalSeparatorRule.Comma;
            }
        }
    }

    public bool IsDecimalDot
    {
        get => DecimalSeparator == OcrDecimalSeparatorRule.Dot;
        set
        {
            if (value)
            {
                DecimalSeparator = OcrDecimalSeparatorRule.Dot;
            }
        }
    }

    public bool IsCostRuleUnitCost
    {
        get => CostAmountRule == OcrCostAmountRule.UnitCost;
        set
        {
            if (value)
            {
                CostAmountRule = OcrCostAmountRule.UnitCost;
            }
        }
    }

    public bool IsCostRuleLineTotal
    {
        get => CostAmountRule == OcrCostAmountRule.LineTotal;
        set
        {
            if (value)
            {
                CostAmountRule = OcrCostAmountRule.LineTotal;
            }
        }
    }

    public OcrScannerConfigDialogViewModel(
        int supplierId,
        string supplierName,
        SupplierColumnMappingDto? columnMapping,
        SupplierOcrTemplateDto? initialTemplate,
        ClientSupplierInvoiceService invoiceService,
        IFilePickerDialog filePicker,
        string? initialSampleFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(invoiceService);
        ArgumentNullException.ThrowIfNull(filePicker);

        SupplierId = supplierId;
        SupplierName = string.IsNullOrWhiteSpace(supplierName) ? $"Proveedor #{supplierId}" : supplierName.Trim();
        ColumnMapping = columnMapping;
        _invoiceService = invoiceService;
        _filePicker = filePicker;

        if (initialTemplate is not null)
        {
            LoadFromDto(initialTemplate);
        }

        if (!string.IsNullOrWhiteSpace(initialSampleFilePath))
        {
            SetSampleFilePath(initialSampleFilePath);
        }
    }

    partial void OnRoiXChanged(double value) => NotifyRoiChanged();
    partial void OnRoiYChanged(double value) => NotifyRoiChanged();
    partial void OnRoiWidthChanged(double value) => NotifyRoiChanged();
    partial void OnRoiHeightChanged(double value) => NotifyRoiChanged();

    private void NotifyRoiChanged()
    {
        OnPropertyChanged(nameof(HasCustomRoi));
        OnPropertyChanged(nameof(RoiSummaryText));
    }

    partial void OnDecimalSeparatorChanged(OcrDecimalSeparatorRule value)
    {
        OnPropertyChanged(nameof(IsDecimalAuto));
        OnPropertyChanged(nameof(IsDecimalComma));
        OnPropertyChanged(nameof(IsDecimalDot));
    }

    partial void OnCostAmountRuleChanged(OcrCostAmountRule value)
    {
        OnPropertyChanged(nameof(IsCostRuleUnitCost));
        OnPropertyChanged(nameof(IsCostRuleLineTotal));
    }

    partial void OnSelectedFactorPatternKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomFactorPattern));
    }

    partial void OnSampleFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasSampleFile));
        OnPropertyChanged(nameof(CanTestExtraction));
        TestExtractionCommand.NotifyCanExecuteChanged();
    }

    partial void OnPreprocessedPreviewBytesChanged(byte[]? value)
    {
        OnPropertyChanged(nameof(HasPreprocessedPreview));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanTestExtraction));
        OnPropertyChanged(nameof(CanSaveTemplate));
        OnPropertyChanged(nameof(CanApplyToInvoice));
        TestExtractionCommand.NotifyCanExecuteChanged();
        SaveTemplateCommand.NotifyCanExecuteChanged();
        ApplyToInvoiceCommand.NotifyCanExecuteChanged();
    }

    public void LoadFromDto(SupplierOcrTemplateDto template)
    {
        ArgumentNullException.ThrowIfNull(template);

        RoiX = template.RoiX is double rx && double.IsFinite(rx) ? Math.Clamp(rx, 0d, 1d) : 0d;
        RoiY = template.RoiY is double ry && double.IsFinite(ry) ? Math.Clamp(ry, 0d, 1d) : 0d;
        RoiWidth = template.RoiWidth is double rw && double.IsFinite(rw) && rw is > 0.01 and <= 1d ? rw : 1d;
        RoiHeight = template.RoiHeight is double rh && double.IsFinite(rh) && rh is > 0.01 and <= 1d ? rh : 1d;
        RotationDegrees = Math.Clamp(template.RotationDegrees, -360d, 360d);
        ContrastFactor = template.ContrastFactor > 0.05 ? Math.Clamp(template.ContrastFactor, 0.1d, 5.0d) : 1.0d;
        BrightnessDelta = Math.Clamp(template.BrightnessDelta, -255d, 255d);
        UseCustomBinarizationThreshold = template.BinarizationThreshold is >= 1 and <= 254;
        BinarizationThreshold = template.BinarizationThreshold is >= 1 and <= 254 ? template.BinarizationThreshold : 128;
        HeaderAnchorKeywords = template.HeaderAnchorKeywords ?? string.Empty;
        FooterAnchorKeywords = template.FooterAnchorKeywords ?? string.Empty;
        ProductColumnPosition = template.ProductColumnPosition;
        QuantityColumnPosition = template.QuantityColumnPosition;
        UnitCostColumnPosition = template.UnitCostColumnPosition;
        SupplierCodeColumnPosition = template.SupplierCodeColumnPosition;
        DecimalSeparator = template.DecimalSeparator;
        CostAmountRule = template.CostAmountRule;

        CharacterReplacements.Clear();
        if (template.CharacterReplacements is not null)
        {
            foreach (var rule in template.CharacterReplacements)
            {
                if (rule is not null && !string.IsNullOrEmpty(rule.From))
                {
                    CharacterReplacements.Add(new OcrCharacterReplacementRuleItemViewModel(rule.From, rule.To));
                }
            }
        }

        if (string.IsNullOrWhiteSpace(template.FactorExtractionPattern))
        {
            SelectedFactorPatternKey = "NONE";
            CustomFactorRegex = string.Empty;
        }
        else
        {
            var upper = template.FactorExtractionPattern.Trim().ToUpperInvariant();
            if (upper is "NONE" or "AUTO" or "1X40" or "BTO_X_N" or "CAJA_N_UND")
            {
                SelectedFactorPatternKey = upper;
                CustomFactorRegex = string.Empty;
            }
            else
            {
                SelectedFactorPatternKey = "CUSTOM";
                CustomFactorRegex = template.FactorExtractionPattern.Trim();
            }
        }
    }

    public SupplierOcrTemplateDto BuildTemplateDto()
    {
        var factorPattern = SelectedFactorPatternKey switch
        {
            "NONE" or "" => null,
            "CUSTOM" => string.IsNullOrWhiteSpace(CustomFactorRegex) ? null : CustomFactorRegex.Trim(),
            var preset => preset
        };

        var replacements = CharacterReplacements
            .Where(item => !string.IsNullOrEmpty(item.From))
            .Select(item => item.ToDto())
            .ToArray();

        var hasRoi = HasCustomRoi;

        return new SupplierOcrTemplateDto(
            Math.Round(Math.Clamp(RotationDegrees, -360d, 360d), 2),
            Math.Round(Math.Clamp(ContrastFactor, 0.1d, 5.0d), 2),
            Math.Round(Math.Clamp(BrightnessDelta, -255d, 255d), 1),
            UseCustomBinarizationThreshold ? Math.Clamp(BinarizationThreshold, 1, 254) : 0,
            hasRoi ? Math.Round(Math.Clamp(RoiX, 0d, 1d), 4) : null,
            hasRoi ? Math.Round(Math.Clamp(RoiY, 0d, 1d), 4) : null,
            hasRoi ? Math.Round(Math.Clamp(RoiWidth, 0.01d, 1d), 4) : null,
            hasRoi ? Math.Round(Math.Clamp(RoiHeight, 0.01d, 1d), 4) : null,
            string.IsNullOrWhiteSpace(HeaderAnchorKeywords) ? null : HeaderAnchorKeywords.Trim(),
            string.IsNullOrWhiteSpace(FooterAnchorKeywords) ? null : FooterAnchorKeywords.Trim(),
            ProductColumnPosition is >= 1 and <= 20 ? ProductColumnPosition : null,
            QuantityColumnPosition is >= 1 and <= 20 ? QuantityColumnPosition : null,
            UnitCostColumnPosition is >= 1 and <= 20 ? UnitCostColumnPosition : null,
            SupplierCodeColumnPosition is >= 1 and <= 20 ? SupplierCodeColumnPosition : null,
            CostAmountRule,
            DecimalSeparator,
            replacements.Length > 0 ? replacements : null,
            factorPattern);
    }

    public void SetNormalizedRoi(double x, double y, double width, double height)
    {
        var clampedX = Math.Clamp(x, 0d, 0.98d);
        var clampedY = Math.Clamp(y, 0d, 0.98d);
        var clampedW = Math.Clamp(width, 0.02d, 1d - clampedX);
        var clampedH = Math.Clamp(height, 0.02d, 1d - clampedY);

        RoiX = Math.Round(clampedX, 4);
        RoiY = Math.Round(clampedY, 4);
        RoiWidth = Math.Round(clampedW, 4);
        RoiHeight = Math.Round(clampedH, 4);
    }

    [RelayCommand]
    private void ResetRoi()
    {
        RoiX = 0d;
        RoiY = 0d;
        RoiWidth = 1d;
        RoiHeight = 1d;
    }

    [RelayCommand]
    private void RotateLeft()
    {
        RotationDegrees = NormalizeAngle(RotationDegrees - 90d);
    }

    [RelayCommand]
    private void RotateRight()
    {
        RotationDegrees = NormalizeAngle(RotationDegrees + 90d);
    }

    [RelayCommand]
    private void ResetVisualAdjustments()
    {
        RotationDegrees = 0d;
        ContrastFactor = 1.0d;
        BrightnessDelta = 0d;
        UseCustomBinarizationThreshold = false;
        BinarizationThreshold = 128;
    }

    [RelayCommand]
    private void AddReplacementRule()
    {
        if (string.IsNullOrEmpty(NewReplacementFrom))
        {
            return;
        }

        CharacterReplacements.Add(new OcrCharacterReplacementRuleItemViewModel(NewReplacementFrom, NewReplacementTo));
        NewReplacementFrom = string.Empty;
        NewReplacementTo = string.Empty;
    }

    [RelayCommand]
    private void RemoveReplacementRule(OcrCharacterReplacementRuleItemViewModel? rule)
    {
        if (rule is not null)
        {
            CharacterReplacements.Remove(rule);
        }
    }

    [RelayCommand]
    private async Task LoadSampleFileAsync(CancellationToken cancellationToken)
    {
        var path = _filePicker.PickFilePath("Seleccionar factura de muestra (OCR)", SupplierInvoiceViewModel.OcrFileFilter);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SetSampleFilePath(path);
        await TestExtractionAsync(cancellationToken);
    }

    public void SetSampleFilePath(string filePath)
    {
        SampleFilePath = filePath;
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (File.Exists(filePath) && ext is not ".pdf")
            {
                OriginalSampleBytes = File.ReadAllBytes(filePath);
            }
            else
            {
                OriginalSampleBytes = null;
            }
        }
        catch
        {
            OriginalSampleBytes = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestExtraction))]
    private async Task TestExtractionAsync(CancellationToken cancellationToken)
    {
        if (!HasSampleFile)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Ejecutando prueba de lectura OCR con los parámetros actuales...";
        try
        {
            var template = BuildTemplateDto();
            var result = await _invoiceService.ExtractOcrAsync(
                SampleFilePath,
                SupplierId,
                ColumnMapping,
                template,
                cancellationToken);

            LastExtractionResult = result;
            SandboxLines.Clear();
            foreach (var line in result.Lines)
            {
                SandboxLines.Add(line);
            }

            RawExtractedText = result.RawExtractedText ?? string.Empty;
            if (result.PreprocessedPreviewPng is { Length: > 0 })
            {
                PreprocessedPreviewBytes = result.PreprocessedPreviewPng;
            }

            DetectedRif = result.DetectedRif;
            DetectedInvoiceNumber = result.DetectedInvoiceNumber;
            DetectedExchangeRate = result.DetectedExchangeRate;

            StatusMessage = SandboxLines.Count > 0
                ? $"Prueba completada: {SandboxLines.Count} línea(s) extraída(s) en la tabla de prueba."
                : "La prueba terminó pero no se detectaron filas de tabla. Ajuste las anclas, el orden de columnas o el umbral binario.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al probar la extracción OCR: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanApplyToInvoice));
            ApplyToInvoiceCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveTemplate))]
    private async Task SaveTemplateAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        StatusMessage = "Guardando plantilla OCR del proveedor...";
        try
        {
            var template = BuildTemplateDto();
            SavedTemplate = await _invoiceService.SaveSupplierOcrTemplateAsync(SupplierId, template, cancellationToken);
            StatusMessage = $"Plantilla OCR guardada para {SupplierName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo guardar la plantilla OCR: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyToInvoice))]
    private async Task ApplyToInvoiceAsync(CancellationToken cancellationToken)
    {
        if (LastExtractionResult is null)
        {
            return;
        }

        await SaveTemplateAsync(cancellationToken);
        if (SavedTemplate is not null)
        {
            ApplyResultToStaging = true;
        }
    }

    private static double NormalizeAngle(double degrees)
    {
        var mod = degrees % 360d;
        if (mod > 180d)
        {
            mod -= 360d;
        }
        else if (mod <= -180d)
        {
            mod += 360d;
        }

        return mod;
    }
}
