using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class PartialDeliveryDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _notes;

    public ObservableCollection<PartialDeliveryLineViewModel> Items { get; }

    public PartialDeliveryDialogResult? Result { get; private set; }

    public bool CanConfirm => Items.Any(line => line.InputQuantity > 0m)
        && Items.All(line => line.IsValid);

    public PartialDeliveryDialogViewModel(PendingPickupClientDto pickup)
    {
        ArgumentNullException.ThrowIfNull(pickup);

        Items = new ObservableCollection<PartialDeliveryLineViewModel>(
            (pickup.Items ?? new()).Select(item => new PartialDeliveryLineViewModel(item)));
        foreach (var line in Items)
        {
            line.PropertyChanged += OnLinePropertyChanged;
        }

        if (pickup.PendingDraft is { } draft)
        {
            Notes = draft.Notes;
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

    private void OnLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PartialDeliveryLineViewModel.InputQuantity)
            or nameof(PartialDeliveryLineViewModel.IsValid))
        {
            OnPropertyChanged(nameof(CanConfirm));
            ConfirmCommand.NotifyCanExecuteChanged();
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
}

public partial class PartialDeliveryLineViewModel : ObservableObject
{
    private decimal _inputQuantity;
    private string _inputQuantityText = "0";
    private bool _isUpdatingInputQuantityText;

    [ObservableProperty]
    private string? _errorMessage;

    public int SaleItemId { get; }
    public int ProductId { get; }
    public string ProductName { get; }
    public decimal PendingQuantity { get; }
    public bool CanDeliver => PendingQuantity > 0m;
    public bool IsValid => ErrorMessage is null;

    public string InputQuantityText
    {
        get => _inputQuantityText;
        set
        {
            if (!SetProperty(ref _inputQuantityText, value, nameof(InputQuantityText))) return;

            if (string.IsNullOrWhiteSpace(value))
            {
                _isUpdatingInputQuantityText = true;
                InputQuantity = 0m;
                _isUpdatingInputQuantityText = false;
                return;
            }

            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsedQuantity))
            {
                SetValidationMessage("Ingrese una cantidad válida.");
                return;
            }

            _isUpdatingInputQuantityText = true;
            InputQuantity = parsedQuantity;
            _isUpdatingInputQuantityText = false;

            if (InputQuantity != parsedQuantity)
            {
                SetProperty(
                    ref _inputQuantityText,
                    InputQuantity.ToString("0.###", CultureInfo.CurrentCulture),
                    nameof(InputQuantityText));
            }
        }
    }

    public decimal InputQuantity
    {
        get => _inputQuantity;
        set
        {
            var clampedQuantity = value;
            string? validationMessage = null;
            if (value > PendingQuantity)
            {
                clampedQuantity = PendingQuantity;
                validationMessage = "Supera la cantidad pendiente";
            }
            else if (value < 0m)
            {
                clampedQuantity = 0m;
                validationMessage = "La cantidad no puede ser negativa.";
            }

            if (SetProperty(ref _inputQuantity, clampedQuantity, nameof(InputQuantity))
                && !_isUpdatingInputQuantityText)
            {
                SetProperty(
                    ref _inputQuantityText,
                    clampedQuantity.ToString("0.###", CultureInfo.CurrentCulture),
                    nameof(InputQuantityText));
            }

            SetValidationMessage(validationMessage);
        }
    }

    public PartialDeliveryLineViewModel(PendingPickupItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);

        SaleItemId = item.SaleItemId;
        ProductId = item.ProductId;
        ProductName = item.ProductName;
        PendingQuantity = Math.Max(0m, item.PendingQuantity);
    }

    private void SetValidationMessage(string? validationMessage)
    {
        if (!string.Equals(ErrorMessage, validationMessage, StringComparison.Ordinal))
        {
            ErrorMessage = validationMessage;
            OnPropertyChanged(nameof(IsValid));
        }
    }
}
