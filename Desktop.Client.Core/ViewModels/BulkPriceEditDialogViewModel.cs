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
    public const string NoOperationsMessage = "Configure al menos una operación o una modificación en Redondeo y protección de margen.";
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

    // 8.182-T2 (S6) + Spec V0.15: separación de capas — el redondeo USD por canal (exacto /
    // psicológico) y la proyección Bs.S de efectivo (capa cash) usan listas exclusivas.
    private static readonly IReadOnlyList<BulkPriceRoundingOption> UsdRoundingRuleOptionsShared = new[]
    {
        new BulkPriceRoundingOption(RoundingRuleType.ExactMath, "Sin redondeo (exacto)"),
        new BulkPriceRoundingOption(RoundingRuleType.UsdPsychological, "Psicológico USD (.99/.95/.75/.50/.25)")
    };

    private static readonly IReadOnlyList<BulkPriceRoundingOption> CashBsRoundingRuleOptionsShared = new[]
    {
        new BulkPriceRoundingOption(RoundingRuleType.ExactMath, "Sin redondeo"),
        new BulkPriceRoundingOption(RoundingRuleType.VesCashFriendlyInteger, "Efectivo rápido Bs.S (entero)"),
        new BulkPriceRoundingOption(RoundingRuleType.VesCashFriendlyStep5, "(múltiplo de 5)"),
        new BulkPriceRoundingOption(RoundingRuleType.VesCashFriendlyStep10, "(múltiplo de 10)")
    };

    private readonly IProductService _productService;
    private readonly IDialogService? _dialogService;
    private readonly BulkPriceSelection _selection;
    private readonly RoundingRuleType _globalUsdRoundingRule;
    private readonly RoundingRuleType _globalCashBsRoundingRule;
    private BulkPriceUpdateRequestDto? _previewedRequest;

    /// <summary>Resultado del cierre: true cuando la actualización se aplicó y persistió.</summary>
    public Action<bool>? RequestClose { get; set; }

    public ObservableCollection<BulkPriceOperationRowViewModel> Operations { get; } = new();

    public ObservableCollection<BulkPricePreviewRowViewModel> PreviewRows { get; } = new();

    /// <summary>Spec V0.15: opciones USD-only de los combos de redondeo por canal (Detal/Mayor).</summary>
    public IReadOnlyList<BulkPriceRoundingOption> UsdRoundingRuleOptions => UsdRoundingRuleOptionsShared;

    /// <summary>Spec V0.15: opciones del combo de proyección Bs.S (capa cash independiente del USD).</summary>
    public IReadOnlyList<BulkPriceRoundingOption> CashBsRoundingRuleOptions => CashBsRoundingRuleOptionsShared;

    public int SelectedCount => _selection.SelectedCount;

    public bool IsSelectAllFiltered => _selection.SelectAllFiltered;

    public string SelectionSummary => IsSelectAllFiltered
        ? $"Todos los filtrados seleccionados ({SelectedCount})"
        : $"{SelectedCount} producto(s) seleccionado(s)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(PreviewSummary))]
    private bool _hasPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreview))]
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

    /// <summary>Spec V0.15: el mensaje inferior se pinta ámbar cuando la vista previa no trae cambios aplicables.</summary>
    [ObservableProperty]
    private bool _statusMessageIsWarning;

    // 8.182-T2 (S6) + Spec V0.15: redondeo USD por canal, capa cash Bs.S y piso de margen por lote
    // (contrato T1). Default ExactMath sin piso = comportamiento 8.181 intacto.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetailFloorInput))]
    private RoundingRuleType _retailRoundingRule = RoundingRuleType.ExactMath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWholesaleFloorInput))]
    private RoundingRuleType _wholesaleRoundingRule = RoundingRuleType.ExactMath;

    /// <summary>
    /// Spec V0.15: capa de proyección Bs.S de efectivo (independiente del redondeo USD); se
    /// inicializa con la política global (CashBsRoundingRule) y viaja en el request del lote.
    /// </summary>
    [ObservableProperty]
    private RoundingRuleType _cashBsRoundingRule = RoundingRuleType.ExactMath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetailFloorInput))]
    [NotifyPropertyChangedFor(nameof(ShowWholesaleFloorInput))]
    private bool _enforceMinimumMarginFloor;

    /// <summary>
    /// Spec V0.15: el piso al detal se muestra SOLO con redondeo psicológico USD + protección activa.
    /// OJO: oculto no significa ignorado — el valor tipeado NO se borra y sigue viajando/aplicando al
    /// clamp de reducciones de margen (la visibilidad es cosmética).
    /// </summary>
    public bool ShowRetailFloorInput => EnforceMinimumMarginFloor && IsUsdPsychologicalRule(RetailRoundingRule);

    /// <inheritdoc cref="ShowRetailFloorInput"/>
    public bool ShowWholesaleFloorInput => EnforceMinimumMarginFloor && IsUsdPsychologicalRule(WholesaleRoundingRule);

    private static bool IsUsdPsychologicalRule(RoundingRuleType rule) => rule == RoundingRuleType.UsdPsychological;

    /// <summary>Piso de margen al detal (%); vacío = sin piso. Se ignora si el toggle está apagado.</summary>
    [ObservableProperty]
    private string _retailMarginFloorText = string.Empty;

    /// <summary>Piso de margen al mayor (%); vacío = sin piso. Se ignora si el toggle está apagado.</summary>
    [ObservableProperty]
    private string _wholesaleMarginFloorText = string.Empty;

    /// <summary>true cuando la vista previa tiene filas bloqueantes (400: negativos, topes, mayor&gt;detal).</summary>
    public bool HasPreviewErrors => PreviewRows.Any(row => row.IsError);

    /// <summary>Spec V0.15: el overlay de estado vacío de la grilla solo desaparece con filas reales.</summary>
    public bool HasPreviewItems => PreviewRows.Count > 0;

    public bool HasApplied { get; private set; }

    public int AppliedCount { get; private set; }

    /// <summary>
    /// Spec V0.15: habilitación OR de la Vista previa — al menos una operación marcada con valor
    /// válido O una modificación activa en "Redondeo y protección de margen" (regla USD/cash no
    /// exacta, o protección de piso con piso numérico definido en algún canal).
    /// </summary>
    public bool CanPreview => !IsBusy && (HasValidOperationConfiguration || HasRoundingConfiguration);

    /// <summary>Spec V0.15: Aplicar hereda la guarda OR y exige una vista previa vigente con cambios.</summary>
    public bool CanApply => CanPreview && HasPreview && !HasPreviewErrors && PreviewUpdatedCount > 0;

    /// <summary>8.181-T2 (fix verificación): el cierre (botón/X/Esc) se bloquea con una operación en vuelo.</summary>
    public bool CanClose => !IsBusy;

    /// <summary>
    /// Resumen de la barra intermedia: sin vista previa queda vacío (la vista oculta el TextBlock) y
    /// nunca repite el conteo de seleccionados, que ya vive en <see cref="SelectionSummary"/>.
    /// </summary>
    public string PreviewSummary => HasPreview
        ? $"A actualizar: {PreviewUpdatedCount} · Omitidos: {PreviewSkippedCount}"
        : string.Empty;

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

        // 8.184-T4 (S6) + Spec V0.15: la política global pre-inicializa las reglas USD por canal, la
        // capa cash Bs.S y el toggle de piso (el usuario puede cambiarlos igual). Sin políticas rigen
        // los defaults ExactMath/sin piso = comportamiento 8.181 intacto. Los baselines globales se
        // retienen para que el motor compare "null hereda global" sin materializar la herencia.
        _globalUsdRoundingRule = pricingPolicies?.UsdRoundingRule ?? RoundingRuleType.ExactMath;
        _globalCashBsRoundingRule = pricingPolicies?.CashBsRoundingRule ?? RoundingRuleType.ExactMath;
        if (pricingPolicies is not null)
        {
            RetailRoundingRule = pricingPolicies.UsdRoundingRule;
            WholesaleRoundingRule = pricingPolicies.UsdRoundingRule;
            CashBsRoundingRule = pricingPolicies.CashBsRoundingRule;
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

        if (operations.Count == 0 && !HasRoundingConfiguration)
        {
            // Guarda OR (Spec V0.15): sin operaciones marcadas, la tarjeta 2 debe aportar la modificación.
            ErrorMessage = NoOperationsMessage;
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
            StatusMessageIsWarning = result.UpdatedCount == 0;
        }
        catch (Exception ex)
        {
            // 400 (selección >1000, resultado inválido) y errores de red llegan legibles vía ApiErrorParser.
            InvalidatePreview();
            ErrorMessage = ex.Message;
            StatusMessage = string.Empty;
            StatusMessageIsWarning = false;
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
            StatusMessageIsWarning = false;
        }
        catch (Exception ex)
        {
            // 400/409 y fallos de red quedan visibles sin cerrar el diálogo (la vista previa sigue vigente).
            ErrorMessage = ex.Message;
            StatusMessage = string.Empty;
            StatusMessageIsWarning = false;
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
        => OnConfigurationChanged();

    // 8.182-T2 (S6) + Spec V0.15: hooks generados por [ObservableProperty]; la vista previa solo
    // representa la configuración con la que se generó y la guarda OR (CanPreview) depende de estos
    // ajustes, así que cualquier cambio notifica la guarda y descarta una vista previa vigente.
    partial void OnRetailRoundingRuleChanged(RoundingRuleType value) => OnConfigurationChanged();

    partial void OnWholesaleRoundingRuleChanged(RoundingRuleType value) => OnConfigurationChanged();

    partial void OnCashBsRoundingRuleChanged(RoundingRuleType value) => OnConfigurationChanged();

    partial void OnEnforceMinimumMarginFloorChanged(bool value) => OnConfigurationChanged();

    partial void OnRetailMarginFloorTextChanged(string value) => OnConfigurationChanged();

    partial void OnWholesaleMarginFloorTextChanged(string value) => OnConfigurationChanged();

    // Spec V0.15: guarda OR — operación marcada con valor válido o modificación de redondeo/protección.
    private bool HasValidOperationConfiguration =>
        Operations.Any(row => row.IsEnabled && TryParseValue(row.ValueText, out var value) && value >= 0m);

    private bool HasRoundingConfiguration =>
        RetailRoundingRule != RoundingRuleType.ExactMath
        || WholesaleRoundingRule != RoundingRuleType.ExactMath
        || CashBsRoundingRule != RoundingRuleType.ExactMath
        || (EnforceMinimumMarginFloor && (HasDefinedFloor(RetailMarginFloorText) || HasDefinedFloor(WholesaleMarginFloorText)));

    private static bool HasDefinedFloor(string? text) =>
        TryParseValue(text, out var value) && value > 0m;

    private void OnConfigurationChanged()
    {
        OnPropertyChanged(nameof(CanPreview));
        InvalidatePreviewOnConfigurationChange();
    }

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
        // Spec V0.15: sin filas marcadas la lista viaja vacía — el modo "Solo Redondeo" es válido si
        // la tarjeta 2 aporta una modificación (validación OR en Preview y en CanPreview).
        foreach (var row in Operations.Where(row => row.IsEnabled))
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
        // 8.182-T2 (S6) + Spec V0.15: reglas USD por canal, capa cash Bs.S y pisos del lote; los pisos
        // viajan solo con el toggle activo y valor válido (vacío = null = sin piso para ese canal).
        RetailRoundingRule = RetailRoundingRule,
        WholesaleRoundingRule = WholesaleRoundingRule,
        CashBsRoundingRule = CashBsRoundingRule,
        // Spec V0.15: baselines globales sembrados — el motor compara la herencia contra ellos y solo
        // materializa la política del artículo cuando difiere de la efectiva.
        GlobalUsdRoundingRule = _globalUsdRoundingRule,
        GlobalCashBsRoundingRule = _globalCashBsRoundingRule,
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
            // Spec V0.15: la unidad de la traza de redondeo (USD/Bs.S) la define la capa de proyección
            // cash (CashBsRoundingRule) con la que se generó la vista previa.
            PreviewRows.Add(BulkPricePreviewRowViewModel.FromDto(row, request.CashBsRoundingRule));
        }

        PreviewUpdatedCount = result.UpdatedCount;
        PreviewSkippedCount = result.SkippedCount;
        ExchangeRate = result.ExchangeRate;
        HasPreview = true;
        OnPropertyChanged(nameof(HasPreviewItems));
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
        OnPropertyChanged(nameof(HasPreviewItems));
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

    /// <summary>Spec V0.15: true para las filas de margen, cuyo Δ en modos porcentuales se expresa en puntos porcentuales aditivos.</summary>
    public bool IsMarginField => Field is BulkPriceField.RetailMargin or BulkPriceField.WholesaleMargin;

    /// <summary>Pista del valor esperado según el modo (absoluto en la unidad del campo o porcentaje/puntos porcentuales).</summary>
    public string ValueHint => IsEnabled && SelectedMode != BulkPriceMode.SetFixed
        ? IsMarginField
            ? "Puntos porcentuales sobre el margen actual"
            : "Porcentaje sobre el valor actual"
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
        RoundingRuleType cashBsRoundingRule)
    {
        var isMargin = dto.Field is BulkPriceField.RetailMargin or BulkPriceField.WholesaleMargin;
        // Spec V0.15: la transición de márgenes se muestra con el formato 2dp del diálogo
        // (CurrencyDisplay, p. ej. "10,00% → 20,00%") para reflejar la semántica de pp aditivos.
        string Format(decimal value) => isMargin
            ? $"{CurrencyDisplay.Number(value)}%"
            : $"${CurrencyDisplay.Number(value)}";

        string? bsSText = dto.BeforeBsS.HasValue || dto.AfterBsS.HasValue
            ? $"Bs.S {CurrencyDisplay.Number(dto.BeforeBsS ?? 0m)} → {CurrencyDisplay.Number(dto.AfterBsS ?? 0m)}"
            : null;

        // Spec V0.15: la traza se rinde en la unidad de la capa que la produjo — con la proyección
        // cash activa (VesCashFriendly*) viaja en Bs.S y reemplaza la traza USD del psicológico; con
        // la capa cash apagada la traza del redondeo USD por canal queda en dólares.
        bool isBsSUnit = IsVesCashFriendlyRule(cashBsRoundingRule);

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

    /// <summary>Spec V0.15: nota de política del lote (p. ej. reglas USD distintas por canal); null si no aplica.</summary>
    public string? PolicyNote { get; init; }

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
    /// Spec V0.15 (tabla consolidada): columna "PRECIO USD (ANTES → DESPUÉS)" — una línea por canal
    /// en USD ("Detal: $1,20 → $1,50"), sin Bs.S. "—" si ningún canal viaja con cambio de precio.
    /// </summary>
    public string UsdTransitionText
    {
        get
        {
            var lines = Changes
                .Where(change => change.Field is BulkPriceField.RetailPrice or BulkPriceField.WholesalePrice)
                .Select(change => $"{change.FieldLabel}: {change.BeforeText} → {change.AfterText}")
                .ToList();

            return lines.Count == 0 ? "—" : string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// 8.183-T2 (R4) + Spec V0.15: columna "BS.S EFECTIVO (PROY.)" — una línea por canal con la
    /// proyección redondeada y su delta de redondeo entre paréntesis cuando es distinto de cero
    /// ("Detal: Bs.S 875,00 (+Bs.S 3,68)"); "—" sin proyección. El matemático exacto viaja en
    /// <see cref="CashFriendlyToolTip"/>. NUNCA representa un valor persistido (canónico 8.104).
    /// </summary>
    public string CashFriendlyText
    {
        get
        {
            var lines = Changes
                .Where(change => change.CashFriendlyAfterBsS.HasValue)
                .Select(change => change.RoundingDelta is { } delta && delta != 0m && change.RoundingDeltaText is { } deltaText
                    ? $"{change.FieldLabel}: {change.CashFriendlyText} ({deltaText})"
                    : $"{change.FieldLabel}: {change.CashFriendlyText}")
                .ToList();

            return lines.Count == 0 ? "—" : string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Spec V0.15: ToolTip de la columna PROY. con el Bs.S matemático exacto por canal (la entrada
    /// de <c>PreRoundingValue</c> de la traza cash); null si ningún canal tiene traza.
    /// </summary>
    public string? CashFriendlyToolTip
    {
        get
        {
            var lines = Changes
                .Where(change => change.CashFriendlyAfterBsS.HasValue && change.PreRoundingValue.HasValue)
                .Select(change => $"{change.FieldLabel}: Bs.S {CurrencyDisplay.Number(change.PreRoundingValue!.Value)}")
                .ToList();

            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }
    }

    public string EffectiveRetailMarginText => FormatMarginPercent(EffectiveRetailMarginPct);

    public string EffectiveWholesaleMarginText => FormatMarginPercent(EffectiveWholesaleMarginPct);

    /// <summary>
    /// Spec V0.15: columna "MARGEN EFECTIVO %" — "Detal: 62,3%" arriba / "Mayor: 22,95%" abajo en la
    /// misma celda; "—" si el backend no informó ningún margen efectivo.
    /// </summary>
    public string EffectiveMarginsText
    {
        get
        {
            var lines = new List<string>();
            if (EffectiveRetailMarginPct.HasValue)
            {
                lines.Add($"Detal: {FormatMarginPercent(EffectiveRetailMarginPct)}");
            }

            if (EffectiveWholesaleMarginPct.HasValue)
            {
                lines.Add($"Mayor: {FormatMarginPercent(EffectiveWholesaleMarginPct)}");
            }

            return lines.Count == 0 ? "—" : string.Join(Environment.NewLine, lines);
        }
    }

    public string StatusText => IsError
        ? $"Error: {SkipReason}"
        : HasSkipReason
            ? $"Omitido: {SkipReason}"
            : "Actualiza";

    public string StatusColor => IsError ? "#DC2626" : HasSkipReason ? "#B45309" : "#16A34A";

    /// <summary>
    /// Spec V0.15: píldora compacta de ESTADO — "Actualiza" / motivo corto en UNA línea (texto antes
    /// del primer ':') / "Error"; el motivo completo y la nota de política viajan en el tooltip.
    /// </summary>
    public string StatusBadgeText => IsError ? "Error" : HasSkipReason ? ShortSkipReason : "Actualiza";

    public string StatusBadgeBackground => IsError ? "#FEE2E2" : HasSkipReason ? "#E2E8F0" : "#DCFCE7";

    public string StatusBadgeForeground => IsError ? "#B91C1C" : HasSkipReason ? "#475569" : "#166534";

    /// <summary>Spec V0.15: tooltip del ESTADO — detalle completo + nota de política cuando existe.</summary>
    public string StatusToolTip => string.IsNullOrWhiteSpace(PolicyNote)
        ? StatusText
        : $"{StatusText}{Environment.NewLine}{PolicyNote}";

    private string ShortSkipReason
    {
        get
        {
            var reason = SkipReason ?? string.Empty;
            var separator = reason.IndexOf(':');
            return separator > 0 ? reason[..separator] : reason;
        }
    }

    public string ChangesSummary => Changes.Count == 0
        ? "—"
        : string.Join(Environment.NewLine, Changes.Select(change => change.Summary));

    public static BulkPricePreviewRowViewModel FromDto(
        BulkPricePreviewRowDto dto,
        RoundingRuleType cashBsRoundingRule) => new()
    {
        ProductId = dto.ProductId,
        SKU = dto.SKU,
        Name = dto.Name,
        SkipReason = dto.SkipReason,
        IsError = dto.IsError,
        PolicyNote = dto.PolicyNote,
        EffectiveRetailMarginPct = dto.EffectiveRetailMarginPct,
        EffectiveWholesaleMarginPct = dto.EffectiveWholesaleMarginPct,
        Changes = dto.Changes
            .Select(change => BulkPriceFieldChangeViewModel.FromDto(change, cashBsRoundingRule))
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
