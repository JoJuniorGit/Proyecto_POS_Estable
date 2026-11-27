using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using Desktop.Client.Validation;

namespace Desktop.Client.ViewModels;

public partial class PartialDeliveryDialogViewModel : ObservableObject, IDisposable
{
    public const int NotesMaxLength = 300;

    private bool _disposed;

    [ObservableProperty]
    private string? _notes;

    public ObservableCollection<PartialDeliveryLineViewModel> Items { get; }

    public PartialDeliveryDialogResult? Result { get; private set; }

    public string InvoiceContextLabel { get; }

    public bool HasInvoiceContext => !string.IsNullOrWhiteSpace(InvoiceContextLabel);

    public string NotesCounterText => $"{Notes?.Length ?? 0} / {NotesMaxLength}";

    public bool CanConfirm => Items.Any(line => line.InputQuantity > 0m)
        && Items.All(line => line.IsValid);

    public decimal TotalToDeliver => Items.Sum(line => line.InputQuantity);

    public decimal TotalPending => Items.Sum(line => line.PendingQuantity);

    public bool HasInvalidLine => Items.Any(line => !line.IsValid);

    public bool IsFullDelivery => TotalToDeliver > 0m
        && Items.Where(line => line.PendingQuantity > 0m)
            .All(line => line.InputQuantity >= line.PendingQuantity);

    public string SummaryText
    {
        get
        {
            if (HasInvalidLine) return "Revisa las cantidades marcadas";
            if (TotalToDeliver <= 0m) return "Ingresa al menos una cantidad para entregar";

            var unitLabel = TotalPending == 1m ? "unidad" : "unidades";
            var summary = $"Entregando {FormatQuantity(TotalToDeliver)} de " +
                          $"{FormatQuantity(TotalPending)} {unitLabel}";

            return IsFullDelivery ? $"{summary} · Retiro total" : summary;
        }
    }

    public string SummaryColor
    {
        get
        {
            if (HasInvalidLine) return "#DC2626";
            if (TotalToDeliver <= 0m) return "#B45309";
            return IsFullDelivery ? "#15803D" : "#334155";
        }
    }

    public PartialDeliveryDialogViewModel(PendingPickupClientDto pickup)
    {
        ArgumentNullException.ThrowIfNull(pickup);

        InvoiceContextLabel = BuildInvoiceContextLabel(pickup);
        Items = new ObservableCollection<PartialDeliveryLineViewModel>(
            (pickup.Items ?? new()).Select(item => new PartialDeliveryLineViewModel(item)));

        foreach (var line in Items)
        {
            line.PropertyChanged += OnLinePropertyChanged;
        }

        if (pickup.PendingDraft is { } draft)
        {
            Notes = TruncateNotes(draft.Notes);
            foreach (var requestedItem in draft.Items)
            {
                var line = Items.FirstOrDefault(item => item.SaleItemId == requestedItem.SaleItemId);
                if (line != null)
                {
                    line.InputQuantity = requestedItem.Quantity;
                }
            }
            pickup.PendingDraft = null;
        }
    }

    partial void OnNotesChanged(string? value) =>
        OnPropertyChanged(nameof(NotesCounterText));

    private static string BuildInvoiceContextLabel(PendingPickupClientDto pickup)
    {
        var invoice = pickup.InvoiceNumber.HasValue
            ? $"Factura N° {pickup.InvoiceNumber.Value.ToString("D5", CultureInfo.InvariantCulture)}"
            : $"Pedido #{pickup.SaleId}";

        var customer = pickup.CustomerName?.Trim();
        return string.IsNullOrWhiteSpace(customer) ? invoice : $"{invoice} · {customer}";
    }

    private static string? TruncateNotes(string? notes) =>
        string.IsNullOrEmpty(notes) || notes.Length <= NotesMaxLength
            ? notes
            : notes[..NotesMaxLength];

    private static string FormatQuantity(decimal quantity) =>
        QuantityInputParser.Format(quantity);

    private void OnLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PartialDeliveryLineViewModel.QuantityText)
            or nameof(PartialDeliveryLineViewModel.InputQuantity)
            or nameof(PartialDeliveryLineViewModel.IsValid))
        {
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(TotalToDeliver));
            OnPropertyChanged(nameof(TotalPending));
            OnPropertyChanged(nameof(HasInvalidLine));
            OnPropertyChanged(nameof(IsFullDelivery));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(SummaryColor));
            ConfirmCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void SetAllToMax()
    {
        foreach (var line in Items)
        {
            line.InputQuantity = line.PendingQuantity;
        }
    }

    [RelayCommand]
    private void ClearAll()
    {
        foreach (var line in Items)
        {
            line.InputQuantity = 0m;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!CanConfirm) return;

        Result = new PartialDeliveryDialogResult
        {
            Items = Items
                .Where(line => line.InputQuantity > 0m)
                .Select(line => new PartialDeliveryItemRequestDto
                {
                    SaleItemId = line.SaleItemId,
                    Quantity = line.InputQuantity
                })
                .ToList(),
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim()
        };
    }

    [RelayCommand]
    private void Cancel() => Result = null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var line in Items)
        {
            line.PropertyChanged -= OnLinePropertyChanged;
        }
    }
}

public partial class PartialDeliveryLineViewModel : ObservableObject
{
    private string _quantityText = "0";
    private decimal _inputQuantity;
    private bool _isSyncingText;

    [ObservableProperty]
    private string? _errorMessage;

    public int SaleItemId { get; }
    public int ProductId { get; }
    public string ProductName { get; }
    public decimal PendingQuantity { get; }
    public bool AllowsDecimals { get; }
    public int MaxDecimalPlaces => QuantityInputParser.DefaultMaxDecimalPlaces;
    public bool CanDeliver => PendingQuantity > 0m;
    public bool HasUnitBadge => AllowsDecimals;
    public string UnitBadgeText => "Fraccionado";
    public string PendingQuantityDisplay => QuantityInputParser.Format(PendingQuantity);
    public bool IsValid => ErrorMessage is null;

    public string QuantityText
    {
        get => _quantityText;
        set
        {
            var nextText = value ?? string.Empty;
            if (!SetProperty(ref _quantityText, nextText, nameof(QuantityText))) return;

            if (string.IsNullOrWhiteSpace(nextText))
            {
                SetInputQuantityCore(0m);
                SetValidationMessage(null);
                return;
            }

            if (!QuantityInputParser.TryParse(
                    nextText,
                    AllowsDecimals,
                    MaxDecimalPlaces,
                    out var parsedQuantity,
                    out var parseError))
            {
                SetInputQuantityCore(0m);
                SetValidationMessage(parseError);
                return;
            }

            SetInputQuantityCore(parsedQuantity);
            SetValidationMessage(parsedQuantity > PendingQuantity
                ? $"Máximo: {PendingQuantityDisplay}"
                : null);
        }
    }

    // Compatibility alias for existing callers/tests; UI binds QuantityText.
    public string InputQuantityText
    {
        get => QuantityText;
        set => QuantityText = value;
    }

    public decimal InputQuantity
    {
        get => _inputQuantity;
        set
        {
            var normalized = value;
            string? validationMessage = null;

            if (value < 0m)
            {
                normalized = 0m;
                validationMessage = "La cantidad no puede ser negativa.";
            }
            else if (!AllowsDecimals && value % 1m != 0m)
            {
                normalized = 0m;
                validationMessage = "Este producto solo admite cantidades enteras.";
            }
            else if (value > PendingQuantity)
            {
                validationMessage = $"Máximo: {PendingQuantityDisplay}";
            }

            SetInputQuantityCore(normalized);

            if (!_isSyncingText)
            {
                _isSyncingText = true;
                SetProperty(
                    ref _quantityText,
                    QuantityInputParser.Format(normalized),
                    nameof(QuantityText));
                _isSyncingText = false;
            }

            SetValidationMessage(validationMessage);
        }
    }

    public decimal RemainingQuantity => PendingQuantity - InputQuantity;

    public bool IsComplete => RemainingQuantity == 0m;

    public bool IsOverDelivery => RemainingQuantity < 0m;

    public string RemainingText => IsOverDelivery
        ? "Excede"
        : IsComplete
            ? "✓ Completo"
            : $"{QuantityInputParser.Format(Math.Max(0m, RemainingQuantity))} pend.";

    public string RemainingBadgeBackground => IsComplete
        ? "#DCFCE7"
        : IsOverDelivery
            ? "#FEE2E2"
            : "Transparent";

    public string RemainingForeground => IsComplete
        ? "#15803D"
        : IsOverDelivery
            ? "#B91C1C"
            : "#64748B";

    public PartialDeliveryLineViewModel(PendingPickupItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);

        SaleItemId = item.SaleItemId;
        ProductId = item.ProductId;
        ProductName = item.ProductName ?? string.Empty;
        PendingQuantity = Math.Max(0m, item.PendingQuantity);
        AllowsDecimals = item.PendingQuantity % 1m != 0m
            || item.Quantity % 1m != 0m
            || item.DeliveredQuantity % 1m != 0m;
    }

    [RelayCommand(CanExecute = nameof(CanDeliver))]
    private void SetMax() => InputQuantity = PendingQuantity;

    private void SetInputQuantityCore(decimal value)
    {
        if (!SetProperty(ref _inputQuantity, value, nameof(InputQuantity))) return;

        OnPropertyChanged(nameof(RemainingQuantity));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(IsOverDelivery));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(RemainingBadgeBackground));
        OnPropertyChanged(nameof(RemainingForeground));
    }

    private void SetValidationMessage(string? validationMessage)
    {
        if (string.Equals(ErrorMessage, validationMessage, StringComparison.Ordinal))
        {
            return;
        }

        ErrorMessage = validationMessage;
        OnPropertyChanged(nameof(IsValid));
    }
}
