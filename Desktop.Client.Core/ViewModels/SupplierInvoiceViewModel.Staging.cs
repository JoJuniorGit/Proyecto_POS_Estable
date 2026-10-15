using CommunityToolkit.Mvvm.ComponentModel;
using Core.Common;
using Core.DTOs;
using Core.Helpers;
using System;
using System.Globalization;
using System.Linq;

namespace Desktop.Client.ViewModels;

public enum CostChangeKind
{
    NewProduct,
    Decreased,
    Unchanged,
    IncreasedDrastic,
    IncreasedModerate
}

public sealed partial class SupplierInvoiceLineViewModel : ObservableObject
{
    private const string BandNone = "None";
    private const string BandYellow = "Yellow";
    private const string BandRed = "Red";

    private readonly SupplierInvoiceLineDto _source;
    private readonly string _initialInvoiceCurrency;
    private readonly decimal _initialAppliedRate;
    private string _invoiceCurrency;
    private decimal _appliedRate;
    private bool _isSyncingText;
    private bool _hasInvalidConversionFactorInput;
    private string _editQuantityText = string.Empty;
    private string _editUnitCostDocumentText = string.Empty;
    private string _conversionFactorText = string.Empty;

    public int LineId => _source.Id;
    public string? SupplierCode => _source.SupplierCode;
    public string? Barcode => _source.Barcode;
    public string? Name => _source.Name;
    public decimal Quantity => _source.Quantity;
    public string InvoiceCurrency => _invoiceCurrency;
    public decimal AppliedRate => _appliedRate;

    /// <summary>
    /// 8.168/8.172: costo unitario normalizado en USD reactivo en vivo ante cambios de
    /// <see cref="EditUnitCostDocument"/>, <see cref="ConversionFactor"/> y <see cref="AppliedRate"/>.
    /// </summary>
    public decimal UnitCostUSD => CalculateUnitCostUsd();

    public decimal UnitCostDocument => _source.UnitCostDocument;
    public string Status => _source.Status;
    public string StatusLabel => ResolvedProductId is null ? "[NUEVO]" : GetStatusLabel(Status);
    public int? ResolvedProductId => _source.ResolvedProductId;
    public decimal? OldCostPriceUSD => _source.OldCostPriceUSD;

    /// <summary>
    /// Clasificación semántica de la variación de costo contra el catálogo.
    /// </summary>
    public CostChangeKind CostChangeKind
    {
        get
        {
            if (ResolvedProductId is null || CanCreateProduct || !OldCostPriceUSD.HasValue)
            {
                return CostChangeKind.NewProduct;
            }

            if (Math.Round(UnitCostUSD, 2, MidpointRounding.AwayFromZero) == Math.Round(OldCostPriceUSD.Value, 2, MidpointRounding.AwayFromZero))
            {
                return CostChangeKind.Unchanged;
            }

            if (UnitCostUSD < OldCostPriceUSD.Value)
            {
                return CostChangeKind.Decreased;
            }

            if (OldCostPriceUSD.Value > 0m && (UnitCostUSD / OldCostPriceUSD.Value) >= 1.5m)
            {
                return CostChangeKind.IncreasedDrastic;
            }

            return CostChangeKind.IncreasedModerate;
        }
    }

    /// <summary>
    /// Texto formateado para el pill semántico de comparación de costos.
    /// </summary>
    public string CostComparisonPillText => CostChangeKind switch
    {
        CostChangeKind.Decreased => $"${OldCostPriceUSD!.Value:N2} → {UnitCostUSD:N2}",
        CostChangeKind.Unchanged => $"Sin cambio · {UnitCostUSD:N2}",
        CostChangeKind.IncreasedDrastic => $"${OldCostPriceUSD!.Value:N2} → {UnitCostUSD:N2}",
        CostChangeKind.IncreasedModerate => $"${OldCostPriceUSD!.Value:N2} → {UnitCostUSD:N2}",
        CostChangeKind.NewProduct => $"Nuevo: {UnitCostUSD:N2}",
        _ => UnitCostUSD.ToString("N2")
    };

    /// <summary>
    /// 8.168: comparación reactiva del costo anterior contra el nuevo costo unitario en USD.
    /// </summary>
    public string CostComparison => OldCostPriceUSD.HasValue
        && (string.Equals(Status, "Update", StringComparison.OrdinalIgnoreCase)
            || Math.Round(OldCostPriceUSD.Value, 2, MidpointRounding.AwayFromZero) != Math.Round(UnitCostUSD, 2, MidpointRounding.AwayFromZero))
        ? $"{OldCostPriceUSD.Value:N2} → {UnitCostUSD:N2}"
        : UnitCostUSD.ToString("N2");

    /// <summary>8.168: precio sugerido detal en USD recalculado en vivo con costo unitario y margen.</summary>
    public decimal SuggestedRetailPriceUSD =>
        PricingCalculator.RoundPriceUp(UnitCostUSD * (1m + (MarginRetailOverride / 100m)));

    /// <summary>8.168: precio sugerido mayor en USD recalculado en vivo con costo unitario y margen.</summary>
    public decimal SuggestedWholesalePriceUSD =>
        PricingCalculator.RoundPriceUp(UnitCostUSD * (1m + (MarginWholesaleOverride / 100m)));

    /// <summary>8.172: precio sugerido detal en Bs.S reactivo en base a la tasa aplicada a la factura.</summary>
    public decimal SuggestedRetailPriceBsS =>
        PricingCalculator.ToBsSCeiling(SuggestedRetailPriceUSD, _appliedRate > 0m ? _appliedRate : 0m);

    /// <summary>8.172: precio sugerido mayor en Bs.S reactivo en base a la tasa aplicada a la factura.</summary>
    public decimal SuggestedWholesalePriceBsS =>
        PricingCalculator.ToBsSCeiling(SuggestedWholesalePriceUSD, _appliedRate > 0m ? _appliedRate : 0m);

    /// <summary>8.168: factor efectivo (> 0); usa 1 como fallback seguro ante entradas vacías o &lt;= 0.</summary>
    public decimal EffectiveConversionFactor => ConversionFactor > 0m ? ConversionFactor : 1m;

    /// <summary>8.168: indica si el factor ingresado es inválido (&lt;= 0 o no numérico) sin bloquear filas vecinas.</summary>
    public bool HasInvalidConversionFactor => ConversionFactor <= 0m || _hasInvalidConversionFactorInput;

    public bool CanApprove => ResolvedProductId is not null;
    public bool CanCreateProduct => ResolvedProductId is null;

    /// <summary>8.175: fila resuelta con coincidencia de catálogo ([ACTUALIZAR]) → habilita la edición del vínculo.</summary>
    public bool CanUpdateLink => ResolvedProductId is not null
        && string.Equals(Status, "Update", StringComparison.OrdinalIgnoreCase);

    /// <summary>8.175: el badge estático de estado solo se muestra cuando la fila no ofrece edición de vínculo.</summary>
    public bool ShowStaticStatusBadge => ResolvedProductId is not null && !CanUpdateLink;

    public string MatchMethod => _source.MatchMethod;
    public string? LinkedGroupKey { get; set; }
    public bool IsLinkedGroupMember => !string.IsNullOrWhiteSpace(LinkedGroupKey);

    /// <summary>
    /// Indica si la línea nació sin producto resuelto ([NUEVO]), manteniendo habilitada la edición
    /// incluso después de asociar/crear el producto durante la sesión de revisión.
    /// </summary>
    public bool WasUnresolved { get; private set; }

    [ObservableProperty]
    private bool _isApproved;

    [ObservableProperty]
    private decimal _marginRetailOverride;

    [ObservableProperty]
    private decimal _marginWholesaleOverride;

    /// <summary>8.165: divisor de conversión caja→unidad editable por línea (1 = sin conversión).</summary>
    [ObservableProperty]
    private decimal _conversionFactor;

    /// <summary>
    /// 8.147-S5/S6 (T7): edición de los campos de la línea en revisión (habilitada para origen OCR
    /// y para productos inexistentes/nuevos).
    /// </summary>
    [ObservableProperty]
    private bool _isEditorEnabled;

    [ObservableProperty]
    private string? _editName;

    [ObservableProperty]
    private decimal _editQuantity;

    [ObservableProperty]
    private decimal _editUnitCostDocument;

    /// <summary>
    /// 8.168: buffer textual de cantidad que preserva estados transitorios ("10.", ".5") y acepta coma o punto.
    /// </summary>
    public string EditQuantityText
    {
        get => _editQuantityText;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_editQuantityText, next, StringComparison.Ordinal))
            {
                return;
            }

            _editQuantityText = next;
            OnPropertyChanged();

            if (_isSyncingText)
            {
                return;
            }

            _isSyncingText = true;
            try
            {
                if (TryParseFlexibleDecimal(_editQuantityText, out var parsed) && parsed >= 0m)
                {
                    EditQuantity = parsed;
                }
                else if (string.IsNullOrWhiteSpace(_editQuantityText))
                {
                    EditQuantity = 0m;
                }
            }
            finally
            {
                _isSyncingText = false;
            }
        }
    }

    /// <summary>
    /// 8.168: buffer textual de costo documental (2 a 4 decimales, coma o punto, sin validación intrusiva).
    /// </summary>
    public string EditUnitCostDocumentText
    {
        get => _editUnitCostDocumentText;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_editUnitCostDocumentText, next, StringComparison.Ordinal))
            {
                return;
            }

            _editUnitCostDocumentText = next;
            OnPropertyChanged();

            if (_isSyncingText)
            {
                return;
            }

            _isSyncingText = true;
            try
            {
                if (TryParseFlexibleDecimal(_editUnitCostDocumentText, out var parsed) && parsed >= 0m)
                {
                    EditUnitCostDocument = parsed;
                }
                else if (string.IsNullOrWhiteSpace(_editUnitCostDocumentText))
                {
                    EditUnitCostDocument = 0m;
                }
            }
            finally
            {
                _isSyncingText = false;
            }
        }
    }

    /// <summary>
    /// 8.168: buffer textual del factor caja→unidad (acepta coma o punto y protege contra división por cero).
    /// </summary>
    public string ConversionFactorText
    {
        get => _conversionFactorText;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_conversionFactorText, next, StringComparison.Ordinal))
            {
                return;
            }

            _conversionFactorText = next;
            OnPropertyChanged();

            if (_isSyncingText)
            {
                return;
            }

            _isSyncingText = true;
            try
            {
                if (TryParseFlexibleDecimal(_conversionFactorText, out var parsed))
                {
                    _hasInvalidConversionFactorInput = parsed <= 0m;
                    ConversionFactor = parsed;
                }
                else
                {
                    _hasInvalidConversionFactorInput = true;
                    ConversionFactor = 0m;
                }
            }
            finally
            {
                _isSyncingText = false;
            }

            OnPropertyChanged(nameof(EffectiveConversionFactor));
            OnPropertyChanged(nameof(HasInvalidConversionFactor));
            NotifyPricingChanged();
        }
    }

    /// <summary>
    /// 8.147-S6: banda de resaltado por campo (None/Yellow/Red). Un campo editado por el revisor
    /// deja de resaltarse (valor verificado a mano); al revertir vuelve la banda original.
    /// </summary>
    public string NameConfidenceBand => IsEdited(EditName, _source.Name) ? BandNone : MapBand(_source.OcrNameConfidence);
    public string QuantityConfidenceBand => EditQuantity != _source.Quantity ? BandNone : MapBand(_source.OcrQuantityConfidence);
    public string UnitCostConfidenceBand => EditUnitCostDocument != _source.UnitCostDocument ? BandNone : MapBand(_source.OcrUnitCostConfidence);

    public SupplierInvoiceLineViewModel(
        SupplierInvoiceLineDto source,
        string invoiceCurrency = CurrencyCodes.Usd,
        decimal appliedRate = 0m)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _initialInvoiceCurrency = string.IsNullOrWhiteSpace(invoiceCurrency) ? CurrencyCodes.Usd : invoiceCurrency;
        _initialAppliedRate = appliedRate;
        _invoiceCurrency = _initialInvoiceCurrency;
        _appliedRate = _initialAppliedRate;
        WasUnresolved = source.ResolvedProductId is null;
        _isEditorEnabled = WasUnresolved;
        _isApproved = source.IsApproved;
        _marginRetailOverride = source.MarginRetailOverride ?? source.OldProfitMarginRetail ?? 0m;
        _marginWholesaleOverride = source.MarginWholesaleOverride ?? source.OldProfitMarginWholesale ?? _marginRetailOverride;
        _conversionFactor = source.ConversionFactor <= 0m ? 1m : source.ConversionFactor;
        _editName = source.Name;
        _editQuantity = source.Quantity;
        _editUnitCostDocument = source.UnitCostDocument;
        _editQuantityText = FormatQuantityText(_editQuantity);
        _editUnitCostDocumentText = FormatDocumentCostText(_editUnitCostDocument);
        _conversionFactorText = FormatFactorText(_conversionFactor);
    }

    /// <summary>
    /// Preserva las ediciones en memoria del revisor (nombre, cantidad, costo documental, factor y
    /// márgenes) cuando la factura se recarga tras crear un producto inexistente desde una línea.
    /// </summary>
    public void PreserveEditsFrom(SupplierInvoiceLineViewModel previous, string? createdProductName = null)
    {
        ArgumentNullException.ThrowIfNull(previous);

        LinkedGroupKey = previous.LinkedGroupKey;
        WasUnresolved = previous.WasUnresolved || CanCreateProduct;
        IsEditorEnabled = previous.IsEditorEnabled || WasUnresolved || IsLinkedGroupMember;
        EditName = !string.IsNullOrWhiteSpace(createdProductName) ? createdProductName : previous.EditName;
        EditQuantity = previous.EditQuantity;
        EditUnitCostDocument = previous.EditUnitCostDocument;
        ConversionFactor = previous.ConversionFactor;
        MarginRetailOverride = previous.MarginRetailOverride;
        MarginWholesaleOverride = previous.MarginWholesaleOverride;
        if (CanApprove && createdProductName is not null)
        {
            IsApproved = true;
        }
        else if (CanApprove)
        {
            IsApproved = previous.IsApproved;
        }
    }

    /// <summary>
    /// Preserva las ediciones en memoria (cantidad, costo documental, factor y márgenes editados)
    /// tras vincular manualmente un ítem escaneado con un producto existente del catálogo, manteniendo
    /// el Costo anterior cargado desde la base de datos y recalculando la comparativa de costos en vivo.
    /// </summary>
    public void PreserveEditsAfterLinkFrom(SupplierInvoiceLineViewModel previous) =>
        PreserveEditsAfterMultiLinkFrom(previous, isPrimaryLine: true);

    public void PreserveEditsAfterMultiLinkFrom(SupplierInvoiceLineViewModel previous, bool isPrimaryLine)
    {
        ArgumentNullException.ThrowIfNull(previous);

        WasUnresolved = true;
        IsEditorEnabled = previous.IsEditorEnabled || WasUnresolved || IsLinkedGroupMember;
        EditName = _source.Name;
        EditQuantity = isPrimaryLine ? previous.EditQuantity : 0m;
        EditUnitCostDocument = previous.EditUnitCostDocument;
        if (previous.ConversionFactor > 0m && previous.ConversionFactor != 1m)
        {
            ConversionFactor = previous.ConversionFactor;
        }

        if (isPrimaryLine && previous.MarginRetailOverride > 0m)
        {
            MarginRetailOverride = previous.MarginRetailOverride;
        }

        if (isPrimaryLine && previous.MarginWholesaleOverride > 0m)
        {
            MarginWholesaleOverride = previous.MarginWholesaleOverride;
        }

        IsApproved = true;
        NotifyPricingChanged();
    }


    /// <summary>
    /// 8.172: actualiza en vivo la moneda y la tasa aplicada a la factura y recalcula costos y
    /// precios sugeridos de la línea de forma reactiva.
    /// </summary>
    public void UpdateCurrencyAndRate(string invoiceCurrency, decimal appliedRate)
    {
        var normalizedCurrency = string.IsNullOrWhiteSpace(invoiceCurrency) ? CurrencyCodes.Usd : invoiceCurrency;
        var currencyChanged = !string.Equals(_invoiceCurrency, normalizedCurrency, StringComparison.OrdinalIgnoreCase);
        var rateChanged = _appliedRate != appliedRate;
        if (!currencyChanged && !rateChanged)
        {
            return;
        }

        _invoiceCurrency = normalizedCurrency;
        _appliedRate = appliedRate;
        if (currencyChanged)
        {
            OnPropertyChanged(nameof(InvoiceCurrency));
        }

        if (rateChanged)
        {
            OnPropertyChanged(nameof(AppliedRate));
        }

        NotifyPricingChanged();
    }

    partial void OnEditNameChanged(string? value) => OnPropertyChanged(nameof(NameConfidenceBand));

    partial void OnEditQuantityChanged(decimal value)
    {
        if (!_isSyncingText)
        {
            _isSyncingText = true;
            try
            {
                _editQuantityText = FormatQuantityText(value);
                OnPropertyChanged(nameof(EditQuantityText));
            }
            finally
            {
                _isSyncingText = false;
            }
        }

        OnPropertyChanged(nameof(QuantityConfidenceBand));
    }

    partial void OnEditUnitCostDocumentChanged(decimal value)
    {
        if (!_isSyncingText)
        {
            _isSyncingText = true;
            try
            {
                _editUnitCostDocumentText = FormatDocumentCostText(value);
                OnPropertyChanged(nameof(EditUnitCostDocumentText));
            }
            finally
            {
                _isSyncingText = false;
            }
        }

        OnPropertyChanged(nameof(UnitCostConfidenceBand));
        NotifyPricingChanged();
    }

    partial void OnConversionFactorChanged(decimal value)
    {
        if (!_isSyncingText)
        {
            _isSyncingText = true;
            try
            {
                _hasInvalidConversionFactorInput = value <= 0m;
                _conversionFactorText = FormatFactorText(value);
                OnPropertyChanged(nameof(ConversionFactorText));
            }
            finally
            {
                _isSyncingText = false;
            }
        }

        OnPropertyChanged(nameof(EffectiveConversionFactor));
        OnPropertyChanged(nameof(HasInvalidConversionFactor));
        NotifyPricingChanged();
    }

    partial void OnMarginRetailOverrideChanged(decimal value)
    {
        OnPropertyChanged(nameof(SuggestedRetailPriceUSD));
        OnPropertyChanged(nameof(SuggestedRetailPriceBsS));
    }

    partial void OnMarginWholesaleOverrideChanged(decimal value)
    {
        OnPropertyChanged(nameof(SuggestedWholesalePriceUSD));
        OnPropertyChanged(nameof(SuggestedWholesalePriceBsS));
    }

    /// <summary>
    /// 8.147-S6/T7b: proyección al confirm con SOLO las correcciones efectivas de campos OCR
    /// (editor habilitado = origen OCR). Un campo sin cambios viaja null.
    /// </summary>
    public ConfirmLineDto ToConfirmLine() => new(
        LineId,
        IsApproved,
        MarginRetailOverride,
        MarginWholesaleOverride,
        IsEditorEnabled && IsEdited(EditName, _source.Name) ? EditName : null,
        IsEditorEnabled && EditQuantity != _source.Quantity ? EditQuantity : null,
        IsEditorEnabled && EditUnitCostDocument != _source.UnitCostDocument ? EditUnitCostDocument : null,
        EffectiveConversionFactor);

    public static string GetStatusLabel(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "NEW" => "[NUEVO]",
        "UPDATE" => "[ACTUALIZAR]",
        "UNCHANGED" => "[UNCHANGED]",
        "CONFLICT" => "[CONFLICT]",
        _ => "[CONFLICT]"
    };

    /// <summary>
    /// 8.168: parseo decimal permisivo para edición interactiva (acepta coma o punto, ".5", "10.").
    /// </summary>
    public static bool TryParseFlexibleDecimal(string? raw, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (!normalized.Any(char.IsAsciiDigit))
        {
            return false;
        }

        var commaIndex = normalized.LastIndexOf(',');
        var dotIndex = normalized.LastIndexOf('.');
        if (commaIndex >= 0 && dotIndex >= 0)
        {
            normalized = commaIndex > dotIndex
                ? normalized.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.')
                : normalized.Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else if (commaIndex >= 0)
        {
            normalized = normalized.Replace(',', '.');
        }

        if (normalized.StartsWith('.'))
        {
            normalized = "0" + normalized;
        }
        else if (normalized.StartsWith("-.", StringComparison.Ordinal))
        {
            normalized = "-0." + normalized[2..];
        }
        else if (normalized.StartsWith("+.", StringComparison.Ordinal))
        {
            normalized = "+0." + normalized[2..];
        }

        if (normalized.EndsWith('.'))
        {
            normalized = normalized[..^1];
        }

        if (! decimal.TryParse(
                normalized,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }

    private decimal CalculateUnitCostUsd()
    {
        if (EditUnitCostDocument == _source.UnitCostDocument
            && EffectiveConversionFactor == 1m
            && _appliedRate == _initialAppliedRate
            && string.Equals(_invoiceCurrency, _initialInvoiceCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return _source.UnitCostUSD;
        }

        if (string.Equals(_invoiceCurrency, CurrencyCodes.BsS, StringComparison.OrdinalIgnoreCase))
        {
            if (_appliedRate <= 0m)
            {
                // 8.176: sin tasa válida una factura Bs.S no tiene costo normalizado; la columna
                // Costo USD muestra 0,00 (sin NaN/Infinity) y el confirm queda bloqueado.
                return 0m;
            }

            return PricingCalculator.CalculateInvoiceUnitCostUsd(
                EditUnitCostDocument,
                EffectiveConversionFactor,
                _invoiceCurrency,
                _appliedRate);
        }

        var usdDocumentCost = PricingCalculator.CalculateInvoiceUnitCostUsd(
            EditUnitCostDocument,
            EffectiveConversionFactor,
            _invoiceCurrency,
            _appliedRate);

        if (_appliedRate <= 0m && _source.UnitCostDocument > 0m && _source.UnitCostUSD != _source.UnitCostDocument)
        {
            return Math.Round(
                _source.UnitCostUSD * (usdDocumentCost / _source.UnitCostDocument),
                4,
                MidpointRounding.AwayFromZero);
        }

        return usdDocumentCost;
    }

    private void NotifyPricingChanged()
    {
        OnPropertyChanged(nameof(UnitCostUSD));
        OnPropertyChanged(nameof(CostComparison));
        OnPropertyChanged(nameof(CostChangeKind));
        OnPropertyChanged(nameof(CostComparisonPillText));
        OnPropertyChanged(nameof(SuggestedRetailPriceUSD));
        OnPropertyChanged(nameof(SuggestedWholesalePriceUSD));
        OnPropertyChanged(nameof(SuggestedRetailPriceBsS));
        OnPropertyChanged(nameof(SuggestedWholesalePriceBsS));
    }

    public static string FormatEditableDecimal(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatDocumentCostText(decimal value) =>
        value.ToString("0.00##", CultureInfo.InvariantCulture);

    private static string FormatQuantityText(decimal value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatFactorText(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool IsEdited(string? value, string? source) =>
        !string.Equals(value, source, StringComparison.Ordinal);

    private static string MapBand(decimal? confidence) => confidence switch
    {
        null => BandNone,
        < OcrConfidence.RedBelow => BandRed,
        < OcrConfidence.YellowBelow => BandYellow,
        _ => BandNone
    };
}

