using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>
/// 8.181-T2 (S5): selección enviada al contrato de edición masiva del catálogo: ids explícitos
/// (1..1000) o modo "todos los filtrados" (Filter + SelectAllFiltered con los filtros vigentes
/// del listado del catálogo).
/// </summary>
public sealed record BulkPriceSelection(
    IReadOnlyList<int>? ProductIds,
    BulkPriceSelectionFilterDto? Filter,
    bool SelectAllFiltered,
    int SelectedCount);

/// <summary>
/// 8.181-T2 (S5): configuración de operaciones por campo (precio/margen/costo × modo fijo o
/// porcentual), vista previa "Antes vs Después" contra <c>POST bulk-prices/preview</c> y aplicación
/// atómica con confirmación contra <c>POST bulk-prices/apply</c> (contrato congelado por T1).
/// Cualquier ajuste de configuración invalida la vista previa vigente: solo se aplica lo previsualizado.
/// </summary>
public partial class BulkPriceEditDialogViewModel : ObservableObject, IDisposable
{
    public const int MaxSelectionSize = 1000;
    public const string NoOperationsMessage = "Seleccione al menos una operación para editar.";
    public const string InvalidValueMessage = "Ingrese un valor numérico válido mayor o igual a cero.";
    public const string InvalidMarginFloorMessage = "Ingrese un piso de margen numérico válido mayor o igual a cero.";
    public const string PreviewRequiredMessage = "Genere la vista previa antes de aplicar los cambios.";
    public const string PreviewErrorMessage = "La vista previa contiene errores que impiden aplicar los cambios.";
    public const string NoChangesMessage = "La vista previa no contiene cambios para aplicar.";

    private static readonly IReadOnlyList<BulkPriceModeOption> SharedModeOptions = new[]
    {
        new BulkPriceModeOption(BulkPriceMode.SetFixed, "Fijar valor"),
        new BulkPriceModeOption(BulkPriceMode.IncreasePercent, "Aumentar %"),
        new BulkPriceModeOption(BulkPriceMode.DecreasePercent, "Disminuir %")
    };

    // 8.182-T2 (S6): reglas de redondeo por canal (mismos labels para ambos combos). El granulado
    // por sub-opción replica el enum del contrato congelado de T1.
    private static readonly IReadOnlyList<BulkPriceRoundingOption> SharedRoundingRuleOptions = new[]
    {
        new BulkPriceRoundingOption(RoundingRuleType.ExactMath, "Sin redondeo (exacto)"),
        new BulkPriceRoundingOption(RoundingRuleType.UsdPsychological, "Psicológico USD (.99/.95/.75/.50/.25)"),
        new BulkPriceRoundingOption(RoundingRuleType.VesCashFriendlyInteger, "Efectivo rápido Bs.S (entero)"),
        new BulkPriceRoundingOption(RoundingRuleType.VesCashFriendlyStep5, "(múltiplo de 5)"),
        new BulkPriceRoundingOption(RoundingRuleType.VesCashFriendlyStep10, "(múltiplo de 10)")
    };

    private readonly IProductService _productService;
    private readonly IDialogService? _dialogService;
    private readonly BulkPriceSelection _selection;
    private BulkPriceUpdateRequestDto? _previewedRequest;

    /// <summary>Resultado del cierre: true cuando la actualización se aplicó y persistió.</summary>
    public Action<bool>? RequestClose { get; set; }

    public ObservableCollection<BulkPriceOperationRowViewModel> Operations { get; } = new();

    public ObservableCollection<BulkPricePreviewRowViewModel> PreviewRows { get; } = new();

    /// <summary>8.182-T2 (S6): opciones del combo de regla de redondeo (una por sub-opción del enum).</summary>
    public IReadOnlyList<BulkPriceRoundingOption> RoundingRuleOptions => SharedRoundingRuleOptions;

    public int SelectedCount => _selection.SelectedCount;

    public bool IsSelectAllFiltered => _selection.SelectAllFiltered;

    public string SelectionSummary => IsSelectAllFiltered
        ? $"Todos los filtrados seleccionados ({SelectedCount})"
        : $"{SelectedCount} producto(s) seleccionado(s)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _hasPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanClose))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewSummary))]
    private int _previewUpdatedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewSummary))]
    private int _previewSkippedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExchangeRateSummary))]
    private decimal _exchangeRate;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // 8.182-T2 (S6): configuración de redondeo por canal y piso de margen por lote (contrato T1).
    // Default ExactMath sin piso = comportamiento 8.181 intacto.
    [ObservableProperty]
    private RoundingRuleType _retailRoundingRule = RoundingRuleType.ExactMath;

    [ObservableProperty]
    private RoundingRuleType _wholesaleRoundingRule = RoundingRuleType.ExactMath;

    [ObservableProperty]
    private bool _enforceMinimumMarginFloor;

    /// <summary>Piso de margen al detal (%); vacío = sin piso. Se ignora si el toggle está apagado.</summary>
    [ObservableProperty]
    private string _retailMarginFloorText = string.Empty;

    /// <summary>Piso de margen al mayor (%); vacío = sin piso. Se ignora si el toggle está apagado.</summary>
    [ObservableProperty]
    private string _wholesaleMarginFloorText = string.Empty;

    /// <summary>true cuando la vista previa tiene filas bloqueantes (400: negativos, topes, mayor&gt;detal).</summary>
    public bool HasPreviewErrors => PreviewRows.Any(row => row.IsError);

    public bool HasApplied { get; private set; }

    public int AppliedCount { get; private set; }

    public bool CanApply => HasPreview && !HasPreviewErrors && PreviewUpdatedCount > 0 && !IsBusy;

    /// <summary>8.181-T2 (fix verificación): el cierre (botón/X/Esc) se bloquea con una operación en vuelo.</summary>
    public bool CanClose => !IsBusy;

    public string PreviewSummary => HasPreview
        ? $"Seleccionados: {SelectedCount} · A actualizar: {PreviewUpdatedCount} · Omitidos: {PreviewSkippedCount}"
        : $"Seleccionados: {SelectedCount}";

    public string ExchangeRateSummary => ExchangeRate > 0
        ? $"Tasa BCV aplicada: {CurrencyDisplay.Number(ExchangeRate)}"
        : "Sin tasa configurada: se conserva el Bs.S previo de cada producto.";

    public BulkPriceEditDialogViewModel(IProductService productService, IDialogService? dialogService, BulkPriceSelection selection, PricingPoliciesDto? pricingPolicies = null)
    {
        ArgumentNullException.ThrowIfNull(productService);
        ArgumentNullException.ThrowIfNull(selection);

        _productService = productService;
        _dialogService = dialogService;
        _selection = selection;

        Operations.Add(new BulkPriceOperationRowViewModel(BulkPriceField.RetailPrice, "Precio Detal", "USD", SharedModeOptions));
        Operations.Add(new BulkPriceOperationRowViewModel(BulkPriceField.WholesalePrice, "Precio Mayor", "USD", SharedModeOptions));
        Operations.Add(new BulkPriceOperationRowViewModel(BulkPriceField.RetailMargin, "Margen Detal", "%", SharedModeOptions));
        Operations.Add(new BulkPriceOperationRowViewModel(BulkPriceField.WholesaleMargin, "Margen Mayor", "%", SharedModeOptions));
        Operations.Add(new BulkPriceOperationRowViewModel(BulkPriceField.CostPrice, "Costo", "USD", SharedModeOptions));

        foreach (var row in Operations)
        {
            row.PropertyChanged += OnOperationRowPropertyChanged;
        }

        // 8.184-T4 (S6): la política global pre-inicializa las reglas de lote y el toggle de piso
        // (el usuario puede cambiarlos igual). Sin políticas rigen los defaults ExactMath/sin piso
        // = comportamiento 8.181 intacto.
        if (pricingPolicies is not null)
        {
            RetailRoundingRule = pricingPolicies.UsdRoundingRule;
            WholesaleRoundingRule = pricingPolicies.UsdRoundingRule;
            EnforceMinimumMarginFloor = pricingPolicies.EnforceMinimumMarginFloor;
        }
    }

    [RelayCommand]
    private async Task Preview()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = string.Empty;

        if (!TryBuildOperations(out var operations))
        {
            return;
        }

        if (!TryBuildRoundingSettings(out var retailFloor, out var wholesaleFloor))
        {
            return;
        }

        var request = BuildRequest(operations, retailFloor, wholesaleFloor);
        IsBusy = true;
        try
        {
            var result = await _productService.PreviewBulkPricesAsync(request);
            ApplyPreviewResult(request, result);
            StatusMessage = result.UpdatedCount > 0
                ? "Vista previa lista: revise los cambios antes de aplicar."
                : "La vista previa no contiene cambios a aplicar.";
        }
        catch (Exception ex)
        {
            // 400 (selección >1000, resultado inválido) y errores de red llegan legibles vía ApiErrorParser.
            InvalidatePreview();
            ErrorMessage = ex.Message;
            StatusMessage = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = string.Empty;

        if (_previewedRequest is null || !HasPreview)
        {
            ErrorMessage = PreviewRequiredMessage;
            return;
        }

        if (HasPreviewErrors)
        {
            ErrorMessage = PreviewErrorMessage;
            return;
        }

        if (PreviewUpdatedCount <= 0)
        {
            ErrorMessage = NoChangesMessage;
            return;
        }

        if (_dialogService is not null &&
            !_dialogService.ShowConfirm(
                "Aplicar cambios de precios",
                $"¿Aplicar las operaciones configuradas a {PreviewUpdatedCount} producto(s)?\n\n" +
                "La actualización es atómica (todo o nada) y recalcula el Bs.S con la tasa del día."))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _productService.ApplyBulkPricesAsync(_previewedRequest);
            AppliedCount = result.UpdatedCount;
            HasApplied = true;
            StatusMessage = $"{AppliedCount} producto(s) actualizado(s) correctamente.";
        }
        catch (Exception ex)
        {
            // 400/409 y fallos de red quedan visibles sin cerrar el diálogo (la vista previa sigue vigente).
            ErrorMessage = ex.Message;
            StatusMessage = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }

        // 8.181-T2 (fix verificación): el cierre se emite con IsBusy ya en false para que la ventana
        // no quede bloqueada por la guarda de cierre en vuelo (OnClosing).
        if (HasApplied)
        {
            RequestClose?.Invoke(true);
        }
    }

    [RelayCommand]
    private void Close()
    {
        // 8.181-T2 (fix verificación): con una operación en vuelo el cierre queda bloqueado; evita
        // perder el resultado (feedback/refresco) y reintentos que dupliquen porcentajes.
        if (IsBusy)
        {
            return;
        }

        RequestClose?.Invoke(HasApplied);
    }

    public void Dispose()
    {
        foreach (var row in Operations)
        {
            row.PropertyChanged -= OnOperationRowPropertyChanged;
        }

        RequestClose = null;
    }

    private void OnOperationRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => InvalidatePreviewOnConfigurationChange();

    // 8.182-T2 (S6): hooks generados por [ObservableProperty]; la vista previa solo representa la
    // configuración con la que se generó, así que cualquier ajuste de reglas/pisos la descarta.
    partial void OnRetailRoundingRuleChanged(RoundingRuleType value) => InvalidatePreviewOnConfigurationChange();

    partial void OnWholesaleRoundingRuleChanged(RoundingRuleType value) => InvalidatePreviewOnConfigurationChange();

    partial void OnEnforceMinimumMarginFloorChanged(bool value) => InvalidatePreviewOnConfigurationChange();

    partial void OnRetailMarginFloorTextChanged(string value) => InvalidatePreviewOnConfigurationChange();

    partial void OnWholesaleMarginFloorTextChanged(string value) => InvalidatePreviewOnConfigurationChange();

    private void InvalidatePreviewOnConfigurationChange()
    {
        if (!HasPreview)
        {
            return;
        }

        // La vista previa solo representa la configuración con la que se generó; al cambiarla se descarta.
        InvalidatePreview();
        OnPropertyChanged(nameof(CanApply));
    }

    private bool TryBuildOperations(out List<BulkPriceOperationDto> operations)
    {
        operations = new List<BulkPriceOperationDto>();
        var enabledRows = Operations.Where(row => row.IsEnabled).ToList();
        if (enabledRows.Count == 0)
        {
            ErrorMessage = NoOperationsMessage;
            return false;
        }

        foreach (var row in enabledRows)
        {
            if (!TryParseValue(row.ValueText, out var value) || value < 0m)
            {
                ErrorMessage = InvalidValueMessage;
                return false;
            }

            operations.Add(new BulkPriceOperationDto
            {
                Field = row.Field,
                Mode = row.SelectedMode,
                Value = value
            });
        }

        return true;
    }

    private BulkPriceUpdateRequestDto BuildRequest(
        List<BulkPriceOperationDto> operations,
        decimal? retailFloor,
        decimal? wholesaleFloor) => new()
    {
        ProductIds = _selection.ProductIds?.ToList(),
        Filter = _selection.Filter,
        SelectAllFiltered = _selection.SelectAllFiltered,
        Operations = operations,
        // 8.182-T2 (S6): reglas/pisos del lote; los pisos viajan solo con el toggle activo y valor
        // válido (vacío = null = sin piso para ese canal).
        RetailRoundingRule = RetailRoundingRule,
        WholesaleRoundingRule = WholesaleRoundingRule,
        EnforceMinimumMarginFloor = EnforceMinimumMarginFloor,
        RetailMarginFloorPct = retailFloor,
        WholesaleMarginFloorPct = wholesaleFloor
    };

    /// <summary>
    /// 8.182-T2 (S6): validación client-side de los pisos de margen. Con el toggle apagado los
    /// pisos se ignoran (no se validan ni viajan en el request); vacío = sin piso (null).
    /// </summary>
    private bool TryBuildRoundingSettings(out decimal? retailFloor, out decimal? wholesaleFloor)
    {
        retailFloor = null;
        wholesaleFloor = null;

        if (!EnforceMinimumMarginFloor)
        {
            return true;
        }

        if (!TryParseOptionalFloor(RetailMarginFloorText, out retailFloor) ||
            !TryParseOptionalFloor(WholesaleMarginFloorText, out wholesaleFloor))
        {
            ErrorMessage = InvalidMarginFloorMessage;
            return false;
        }

        return true;
    }

    private static bool TryParseOptionalFloor(string? text, out decimal? floor)
    {
        floor = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        if (!TryParseValue(trimmed, out var value) || value < 0m)
        {
            return false;
        }

        floor = value;
        return true;
    }

    private void ApplyPreviewResult(BulkPriceUpdateRequestDto request, BulkPriceUpdateResultDto result)
    {
        _previewedRequest = request;

        PreviewRows.Clear();
        foreach (var row in result.Rows)
        {
            // 8.182-T2 (S6): la unidad de la traza de redondeo (USD/Bs.S) depende de la regla del
            // canal con la que se generó la vista previa.
            PreviewRows.Add(BulkPricePreviewRowViewModel.FromDto(
                row,
                request.RetailRoundingRule,
                request.WholesaleRoundingRule));
        }

        PreviewUpdatedCount = result.UpdatedCount;
        PreviewSkippedCount = result.SkippedCount;
        ExchangeRate = result.ExchangeRate;
        HasPreview = true;
        OnPropertyChanged(nameof(HasPreviewErrors));
        OnPropertyChanged(nameof(CanApply));
    }

    private void InvalidatePreview()
    {
        _previewedRequest = null;
        PreviewRows.Clear();
        PreviewUpdatedCount = 0;
        PreviewSkippedCount = 0;
        ExchangeRate = 0m;
        HasPreview = false;
        OnPropertyChanged(nameof(HasPreviewErrors));
        OnPropertyChanged(nameof(CanApply));
    }

    /// <summary>
    /// Acepta separador decimal punto o coma (mismo criterio que el diálogo de precio manual) y
    /// rechaza vacío, texto no numérico o signo: los valores negativos no son válidos en el contrato.
    /// </summary>
    private static bool TryParseValue(string? text, out decimal value)
    {
        value = 0m;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var normalized = trimmed.Replace(',', '.');
        if (normalized.Count(c => c == '.') > 1)
        {
            return false;
        }

        return decimal.TryParse(
            normalized,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }
}

/// <summary>Opción del combo de modo de operación (Fijar valor / Aumentar % / Disminuir %).</summary>
public sealed record BulkPriceModeOption(BulkPriceMode Mode, string Label);

/// <summary>8.182-T2 (S6): opción del combo de regla de redondeo de un canal (Detal/Mayor).</summary>
public sealed record BulkPriceRoundingOption(RoundingRuleType Rule, string Label);

/// <summary>8.181-T2 (S5): fila de configuración de una operación por campo.</summary>
public partial class BulkPriceOperationRowViewModel : ObservableObject
{
    private readonly string _unitHint;

    public BulkPriceField Field { get; }

    public string Label { get; }

    public IReadOnlyList<BulkPriceModeOption> ModeOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueHint))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueHint))]
    private BulkPriceMode _selectedMode;

    [ObservableProperty]
    private string _valueText = string.Empty;

    /// <summary>Pista del valor esperado según el modo (absoluto en la unidad del campo o porcentaje).</summary>
    public string ValueHint => IsEnabled && SelectedMode != BulkPriceMode.SetFixed
        ? "Porcentaje sobre el valor actual"
        : $"Valor absoluto ({_unitHint})";

    public BulkPriceOperationRowViewModel(BulkPriceField field, string label, string unitHint, IReadOnlyList<BulkPriceModeOption> modeOptions)
    {
        Field = field;
        Label = label;
        _unitHint = unitHint;
        ModeOptions = modeOptions;
    }
}

/// <summary>8.181-T2 (S5): línea "antes → después" de un campo afectado por la operación.</summary>
public sealed class BulkPriceFieldChangeViewModel
{
    public BulkPriceField Field { get; init; }

    public string FieldLabel { get; init; } = string.Empty;

    public decimal BeforeValue { get; init; }

    public decimal AfterValue { get; init; }

    public string BeforeText { get; init; } = string.Empty;

    public string AfterText { get; init; } = string.Empty;

    /// <summary>Bs.S canónico antes → después; el contrato solo lo informa en cambios del precio al detal.</summary>
    public string? BsSText { get; init; }

    /// <summary>8.182-T2 (S6): valor matemático previo al redondeo, en la unidad de la regla del canal.</summary>
    public decimal? PreRoundingValue { get; init; }

    /// <summary>8.182-T2 (S6): delta con signo (redondeado − matemático), en la unidad de la regla del canal.</summary>
    public decimal? RoundingDelta { get; init; }

    /// <summary>8.182-T2 (S6): "Matemático" formateado ($ USD o Bs.S según la regla del canal); null si no hay traza.</summary>
    public string? PreRoundingText { get; init; }

    /// <summary>
    /// 8.182-T2 (S6): "Redondeado" formateado en la unidad de la regla del canal (matemático + Δ,
    /// equivalente al valor final del canal: USD en <c>AfterValue</c>; Bs.S en <c>AfterBsS</c>);
    /// null si no hay traza.
    /// </summary>
    public string? RoundedText { get; init; }

    /// <summary>8.182-T2 (S6): "Δ Redondeo" formateado con signo; null si no hay traza.</summary>
    public string? RoundingDeltaText { get; init; }

    /// <summary>
    /// 8.183-T2 (R4): proyección en caliente del Bs.S de efectivo del canal (regla VES + tasa del
    /// día); null si el cambio no lleva proyección. NUNCA refleja un valor persistido.
    /// </summary>
    public decimal? CashFriendlyAfterBsS { get; init; }

    /// <summary>
    /// 8.183-T2 (R4): proyección formateada con el patrón Bs.S vigente del diálogo
    /// ("Bs.S 825,00"); null si no hay proyección.
    /// </summary>
    public string? CashFriendlyText { get; init; }

    public string Summary => BsSText is null
        ? $"{FieldLabel}: {BeforeText} → {AfterText}"
        : $"{FieldLabel}: {BeforeText} → {AfterText} ({BsSText})";

    public static BulkPriceFieldChangeViewModel FromDto(
        BulkPriceFieldChangeDto dto,
        RoundingRuleType retailRule,
        RoundingRuleType wholesaleRule)
    {
        var isMargin = dto.Field is BulkPriceField.RetailMargin or BulkPriceField.WholesaleMargin;
        string Format(decimal value) => isMargin
            ? $"{value.ToString("0.##", CultureInfo.InvariantCulture)}%"
            : $"${CurrencyDisplay.Number(value)}";

        string? bsSText = dto.BeforeBsS.HasValue || dto.AfterBsS.HasValue
            ? $"Bs.S {CurrencyDisplay.Number(dto.BeforeBsS ?? 0m)} → {CurrencyDisplay.Number(dto.AfterBsS ?? 0m)}"
            : null;

        // 8.182-T2 (S6): la traza llega solo cuando la regla del canal movió el precio y está
        // expresada en la unidad de esa regla: USD (psicológico) o Bs.S (efectivo rápido).
        bool isBsSUnit = IsVesCashFriendlyRule(ResolveChannelRule(dto.Field, retailRule, wholesaleRule));

        return new BulkPriceFieldChangeViewModel
        {
            Field = dto.Field,
            FieldLabel = ResolveFieldLabel(dto.Field),
            BeforeValue = dto.BeforeValue,
            AfterValue = dto.AfterValue,
            BeforeText = Format(dto.BeforeValue),
            AfterText = Format(dto.AfterValue),
            BsSText = bsSText,
            PreRoundingValue = dto.PreRoundingValue,
            RoundingDelta = dto.RoundingDelta,
            PreRoundingText = dto.PreRoundingValue.HasValue
                ? FormatRoundingUnit(dto.PreRoundingValue.Value, isBsSUnit)
                : null,
            // Redondeado = matemático + Δ (identidad del backend: el delta es redondeado − matemático).
            RoundedText = dto.PreRoundingValue.HasValue && dto.RoundingDelta.HasValue
                ? FormatRoundingUnit(dto.PreRoundingValue.Value + dto.RoundingDelta.Value, isBsSUnit)
                : null,
            RoundingDeltaText = dto.RoundingDelta.HasValue
                ? FormatRoundingDelta(dto.RoundingDelta.Value, isBsSUnit)
                : null,
            // 8.183-T2 (R4): la proyección llega aparte del estado persistido (incluidas las filas
            // VES informativas donde el USD no cambia) y se muestra siempre en Bs.S.
            CashFriendlyAfterBsS = dto.CashFriendlyAfterBsS,
            CashFriendlyText = dto.CashFriendlyAfterBsS.HasValue
                ? FormatRoundingUnit(dto.CashFriendlyAfterBsS.Value, isBsS: true)
                : null
        };
    }

    private static RoundingRuleType ResolveChannelRule(
        BulkPriceField field,
        RoundingRuleType retailRule,
        RoundingRuleType wholesaleRule) => field switch
    {
        BulkPriceField.WholesalePrice or BulkPriceField.WholesaleMargin => wholesaleRule,
        _ => retailRule
    };

    private static bool IsVesCashFriendlyRule(RoundingRuleType rule)
        => rule is RoundingRuleType.VesCashFriendlyInteger
            or RoundingRuleType.VesCashFriendlyStep5
            or RoundingRuleType.VesCashFriendlyStep10;

    private static string FormatRoundingUnit(decimal value, bool isBsS)
        => isBsS ? $"Bs.S {CurrencyDisplay.Number(value)}" : $"${CurrencyDisplay.Number(value)}";

    private static string FormatRoundingDelta(decimal value, bool isBsS)
    {
        var sign = value > 0m ? "+" : value < 0m ? "-" : string.Empty;
        return $"{sign}{FormatRoundingUnit(Math.Abs(value), isBsS)}";
    }

    private static string ResolveFieldLabel(BulkPriceField field) => field switch
    {
        BulkPriceField.RetailPrice => "Detal",
        BulkPriceField.WholesalePrice => "Mayor",
        BulkPriceField.RetailMargin => "Margen Detal",
        BulkPriceField.WholesaleMargin => "Margen Mayor",
        _ => "Costo"
    };
}

/// <summary>8.181-T2 (S5): fila de la grilla comparativa del preview (incluye omitidos y errores).</summary>
public sealed class BulkPricePreviewRowViewModel
{
    public int ProductId { get; init; }

    public string SKU { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? SkipReason { get; init; }

    public bool IsError { get; init; }

    public IReadOnlyList<BulkPriceFieldChangeViewModel> Changes { get; init; } = Array.Empty<BulkPriceFieldChangeViewModel>();

    /// <summary>8.182-T2 (S6): margen efectivo al detal sobre el precio FINAL; null si no aplica.</summary>
    public decimal? EffectiveRetailMarginPct { get; init; }

    /// <summary>8.182-T2 (S6): margen efectivo al mayor sobre el precio FINAL; null si no aplica.</summary>
    public decimal? EffectiveWholesaleMarginPct { get; init; }

    public bool HasSkipReason => !string.IsNullOrWhiteSpace(SkipReason);

    /// <summary>8.182-T2 (S6): columna "Matemático" (una línea por canal con traza; "—" si no hay).</summary>
    public string MathText => BuildRoundingTrace(change => change.PreRoundingText);

    /// <summary>8.182-T2 (S6): columna "Redondeado" (una línea por canal con traza; "—" si no hay).</summary>
    public string RoundedText => BuildRoundingTrace(change => change.RoundedText);

    /// <summary>8.182-T2 (S6): columna "Δ Redondeo" con signo (una línea por canal; "—" si no hay).</summary>
    public string RoundingDeltaText => BuildRoundingTrace(change => change.RoundingDeltaText);

    /// <summary>
    /// 8.183-T2 (R4): columna "Bs.S efectivo (proy.)" — proyección en caliente por canal con la tasa
    /// del día (una línea por canal; "—" si ningún canal tiene proyección). Es informativa: nunca
    /// representa un valor persistido (el Bs.S guardado sigue siendo el canónico 8.104).
    /// </summary>
    public string CashFriendlyText => BuildRoundingTrace(change => change.CashFriendlyText);

    public string EffectiveRetailMarginText => FormatMarginPercent(EffectiveRetailMarginPct);

    public string EffectiveWholesaleMarginText => FormatMarginPercent(EffectiveWholesaleMarginPct);

    public string StatusText => IsError
        ? $"Error: {SkipReason}"
        : HasSkipReason
            ? $"Omitido: {SkipReason}"
            : "Actualiza";

    public string StatusColor => IsError ? "#DC2626" : HasSkipReason ? "#B45309" : "#16A34A";

    public string ChangesSummary => Changes.Count == 0
        ? "—"
        : string.Join(Environment.NewLine, Changes.Select(change => change.Summary));

    public static BulkPricePreviewRowViewModel FromDto(
        BulkPricePreviewRowDto dto,
        RoundingRuleType retailRule,
        RoundingRuleType wholesaleRule) => new()
    {
        ProductId = dto.ProductId,
        SKU = dto.SKU,
        Name = dto.Name,
        SkipReason = dto.SkipReason,
        IsError = dto.IsError,
        EffectiveRetailMarginPct = dto.EffectiveRetailMarginPct,
        EffectiveWholesaleMarginPct = dto.EffectiveWholesaleMarginPct,
        Changes = dto.Changes
            .Select(change => BulkPriceFieldChangeViewModel.FromDto(change, retailRule, wholesaleRule))
            .ToList()
    };

    private string BuildRoundingTrace(Func<BulkPriceFieldChangeViewModel, string?> selector)
    {
        var lines = Changes
            .Select(change => (change.FieldLabel, Value: selector(change)))
            .Where(entry => entry.Value is not null)
            .Select(entry => $"{entry.FieldLabel}: {entry.Value}")
            .ToList();

        return lines.Count == 0 ? "—" : string.Join(Environment.NewLine, lines);
    }

    private static string FormatMarginPercent(decimal? value)
        => value.HasValue ? $"{value.Value.ToString("0.##", CultureInfo.InvariantCulture)}%" : "—";
}
