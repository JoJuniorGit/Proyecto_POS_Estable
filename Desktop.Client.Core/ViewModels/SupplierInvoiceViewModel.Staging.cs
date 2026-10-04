using CommunityToolkit.Mvvm.ComponentModel;
using Core.DTOs;

namespace Desktop.Client.ViewModels;

public sealed partial class SupplierInvoiceLineViewModel : ObservableObject
{
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

    public SupplierInvoiceLineViewModel(SupplierInvoiceLineDto source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _isApproved = source.IsApproved;
        _marginRetailOverride = source.MarginRetailOverride ?? source.OldProfitMarginRetail ?? 0m;
        _marginWholesaleOverride = source.MarginWholesaleOverride ?? source.OldProfitMarginWholesale ?? _marginRetailOverride;
        RecalculateSuggestedPrices();
    }

    public static string GetStatusLabel(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "NEW" => "[NUEVO]",
        "UPDATE" => "[UPDATE]",
        "UNCHANGED" => "[UNCHANGED]",
        "CONFLICT" => "[CONFLICT]",
        _ => "[CONFLICT]"
    };
}
