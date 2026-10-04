using CommunityToolkit.Mvvm.ComponentModel;
using Core.Common;
using Core.DTOs;

namespace Desktop.Client.ViewModels;

public sealed partial class SupplierInvoiceLineViewModel : ObservableObject
{
    private const string BandNone = "None";
    private const string BandYellow = "Yellow";
    private const string BandRed = "Red";

    private readonly SupplierInvoiceLineDto _source;

    public int LineId => _source.Id;
    public string? SupplierCode => _source.SupplierCode;
    public string? Barcode => _source.Barcode;
    public string? Name => _source.Name;
    public decimal Quantity => _source.Quantity;
    public decimal UnitCostUSD => _source.UnitCostUSD;
    public decimal UnitCostDocument => _source.UnitCostDocument;
    public string Status => _source.Status;
    public string StatusLabel => ResolvedProductId is null ? "[NUEVO]" : GetStatusLabel(Status);
    public int? ResolvedProductId => _source.ResolvedProductId;
    public decimal? OldCostPriceUSD => _source.OldCostPriceUSD;
    public string CostComparison => string.Equals(Status, "Update", System.StringComparison.OrdinalIgnoreCase) && OldCostPriceUSD.HasValue
        ? $"{OldCostPriceUSD.Value:N2} → {UnitCostUSD:N2}"
        : UnitCostUSD.ToString("N2");
    public bool CanApprove => ResolvedProductId is not null;
    public bool CanCreateProduct => ResolvedProductId is null;

    [ObservableProperty]
    private bool _isApproved;

    [ObservableProperty]
    private decimal _marginRetailOverride;

    [ObservableProperty]
    private decimal _marginWholesaleOverride;

    [ObservableProperty]
    private decimal _suggestedRetailPriceUSD;

    [ObservableProperty]
    private decimal _suggestedWholesalePriceUSD;

    /// <summary>
    /// 8.147-S5/S6 (T7): edición de los campos OCR en revisión. Solo el origen OCR lo habilita
    /// (el padre lo propaga al cargar las líneas); las filas tabulares quedan de solo lectura.
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
    /// 8.147-S6: banda de resaltado por campo (None/Yellow/Red). Un campo editado por el revisor
    /// deja de resaltarse (valor verificado a mano); al revertir vuelve la banda original.
    /// </summary>
    public string NameConfidenceBand => IsEdited(EditName, _source.Name) ? BandNone : MapBand(_source.OcrNameConfidence);
    public string QuantityConfidenceBand => EditQuantity != _source.Quantity ? BandNone : MapBand(_source.OcrQuantityConfidence);
    public string UnitCostConfidenceBand => EditUnitCostDocument != _source.UnitCostDocument ? BandNone : MapBand(_source.OcrUnitCostConfidence);

    public SupplierInvoiceLineViewModel(SupplierInvoiceLineDto source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _isApproved = source.IsApproved;
        _marginRetailOverride = source.MarginRetailOverride ?? source.OldProfitMarginRetail ?? 0m;
        _marginWholesaleOverride = source.MarginWholesaleOverride ?? source.OldProfitMarginWholesale ?? _marginRetailOverride;
        _editName = source.Name;
        _editQuantity = source.Quantity;
        _editUnitCostDocument = source.UnitCostDocument;
        RecalculateSuggestedPrices();
    }

    partial void OnEditNameChanged(string? value) => OnPropertyChanged(nameof(NameConfidenceBand));

    partial void OnEditQuantityChanged(decimal value) => OnPropertyChanged(nameof(QuantityConfidenceBand));

    partial void OnEditUnitCostDocumentChanged(decimal value) => OnPropertyChanged(nameof(UnitCostConfidenceBand));

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
        IsEditorEnabled && EditUnitCostDocument != _source.UnitCostDocument ? EditUnitCostDocument : null);

    public static string GetStatusLabel(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "NEW" => "[NUEVO]",
        "UPDATE" => "[UPDATE]",
        "UNCHANGED" => "[UNCHANGED]",
        "CONFLICT" => "[CONFLICT]",
        _ => "[CONFLICT]"
    };

    private static bool IsEdited(string? value, string? source) =>
        !string.Equals(value, source, System.StringComparison.Ordinal);

    private static string MapBand(decimal? confidence) => confidence switch
    {
        null => BandNone,
        < OcrConfidence.RedBelow => BandRed,
        < OcrConfidence.YellowBelow => BandYellow,
        _ => BandNone
    };
}
