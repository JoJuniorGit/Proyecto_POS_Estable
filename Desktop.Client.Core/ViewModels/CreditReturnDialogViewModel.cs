using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// T4 (F3, S5/S7): devolución total o parcial de una venta a crédito. Las cantidades se validan
/// contra lo vendido; el valor y el reembolso del overpago los calcula el servidor sobre la base
/// efectiva. La caja solo paga el excedente efectivamente cobrado.
/// </summary>
public partial class CreditReturnDialogViewModel : ObservableObject, IDisposable
{
    private readonly ISalesService _salesService;
    private readonly IDialogService _dialogService;
    private readonly CreditReceivableClientItemDto _receivable;

    public CreditReturnDialogViewModel(
        ISalesService salesService,
        IDialogService dialogService,
        CreditReceivableClientItemDto receivable,
        SaleDto sale)
    {
        _salesService = salesService ?? throw new ArgumentNullException(nameof(salesService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _receivable = receivable ?? throw new ArgumentNullException(nameof(receivable));

        CustomerName = string.IsNullOrWhiteSpace(receivable.CustomerName) ? "Cliente" : receivable.CustomerName;
        InvoiceDisplay = receivable.InvoiceDisplay;
        BalanceUSD = receivable.BalanceUSD;

        Lines = new ObservableCollection<CreditReturnLineViewModel>(
            (sale?.Items ?? new List<SaleItemDto>()).Select(item => new CreditReturnLineViewModel(item)));
    }

    public string CustomerName { get; }

    public string InvoiceDisplay { get; }

    public decimal BalanceUSD { get; }

    public ObservableCollection<CreditReturnLineViewModel> Lines { get; }

    [ObservableProperty] private string _reason = string.Empty;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private CreditReturnClientResultDto? _result;

    public decimal RequestedReturnUSD => Lines.Sum(line => line.SubtotalUSD);

    public string RequestedReturnDisplay => CurrencyDisplay.FormatPrimary(RequestedReturnUSD);

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsProcessing) return;

        var requested = Lines.Where(line => line.Quantity > 0m).ToList();
        if (requested.Count == 0)
        {
            _dialogService.ShowWarning("Devolución CxC", "Seleccione al menos una línea y su cantidad a devolver.");
            return;
        }

        var invalid = requested.FirstOrDefault(line => line.Quantity > line.MaxQuantity);
        if (invalid != null)
        {
            _dialogService.ShowWarning(
                "Devolución CxC",
                $"La cantidad a devolver de '{invalid.ProductName}' excede la cantidad vendida ({invalid.MaxQuantity:0.###}).");
            return;
        }

        if (Reason.Trim().Length > 500)
        {
            _dialogService.ShowWarning("Devolución CxC", "El motivo de la devolución no puede exceder 500 caracteres.");
            return;
        }

        IsProcessing = true;
        try
        {
            var request = new CreditReturnRequestClientDto
            {
                Items = requested.Select(line => new CreditReturnItemClientRequestDto
                {
                    SaleItemId = line.SaleItemId,
                    Quantity = line.Quantity
                }).ToList(),
                Reason = string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim(),
                IdempotencyKey = Guid.NewGuid().ToString("N")
            };

            Result = await _salesService.RegisterCreditReturnAsync(_receivable.SaleId, request);
            _dialogService.CloseCurrentModal(Result);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Devolución CxC", $"No se pudo registrar la devolución: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        _dialogService.CloseCurrentModal(null);
    }

    public void Dispose()
    {
    }
}

/// <summary>Línea de la venta a crédito con la cantidad solicitada (tope = cantidad vendida).</summary>
public partial class CreditReturnLineViewModel : ObservableObject
{
    public CreditReturnLineViewModel(SaleItemDto item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
    }

    public SaleItemDto Item { get; }

    public int SaleItemId => Item.Id;

    public string ProductName => string.IsNullOrWhiteSpace(Item.ProductName) ? "Producto" : Item.ProductName;

    public decimal MaxQuantity => Item.Quantity;

    public decimal UnitPriceUSD => Item.UnitPrice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Quantity))]
    [NotifyPropertyChangedFor(nameof(SubtotalUSD))]
    private string _quantityText = "0";

    public decimal Quantity => CheckoutViewModel.ParseAmount(QuantityText);

    public decimal SubtotalUSD => Math.Round(Quantity * UnitPriceUSD, 2, MidpointRounding.AwayFromZero);
}

