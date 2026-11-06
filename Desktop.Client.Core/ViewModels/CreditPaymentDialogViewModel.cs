using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// T4 (F3, S3/S6): modal de abono CxC. El operador captura el monto en USD (la deuda está
/// denominada en USD) y ve la equivalencia en Bs.S a la tasa del día; el servidor congela la tasa
/// del cobro y calcula el FX realizado. La imputación es FIFO de cartera o explícita por factura.
/// </summary>
public partial class CreditPaymentDialogViewModel : ObservableObject, IDisposable
{
    private readonly ISalesService _salesService;
    private readonly IDialogService _dialogService;

    public CreditPaymentDialogViewModel(
        ISalesService salesService,
        IDialogService dialogService,
        int customerId,
        string customerName,
        IEnumerable<CreditReceivableClientItemDto> openReceivables,
        IEnumerable<PaymentMethodDto> methods,
        decimal todayRate,
        decimal? suggestedAmountUSD = null)
    {
        _salesService = salesService ?? throw new ArgumentNullException(nameof(salesService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        CustomerId = customerId;
        CustomerName = string.IsNullOrWhiteSpace(customerName) ? "Cliente" : customerName;
        TodayRate = todayRate;

        AvailableMethods = new ObservableCollection<PaymentMethodDto>(methods ?? Enumerable.Empty<PaymentMethodDto>());
        OpenReceivables = new ObservableCollection<CreditPaymentAllocationOption>(
            (openReceivables ?? Enumerable.Empty<CreditReceivableClientItemDto>())
                .Select(row => new CreditPaymentAllocationOption(row, OnAllocationSelectionChanged)));

        decimal initial = suggestedAmountUSD ?? OpenReceivables.Sum(option => option.Receivable.BalanceUSD);
        _amountUsdText = initial <= 0m ? string.Empty : initial.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        _reference = string.Empty;
    }

    public int CustomerId { get; }

    public string CustomerName { get; }

    public decimal TodayRate { get; }

    public ObservableCollection<PaymentMethodDto> AvailableMethods { get; }

    public ObservableCollection<CreditPaymentAllocationOption> OpenReceivables { get; }

    public decimal TotalOpenUSD => OpenReceivables.Sum(option => option.Receivable.BalanceUSD);

    public string TotalOpenDisplay => $"$ {TotalOpenUSD:N2} · Bs.S {Math.Round(TotalOpenUSD * TodayRate, 2, MidpointRounding.AwayFromZero):N2}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmountUSD))]
    [NotifyPropertyChangedFor(nameof(AmountBsS))]
    [NotifyPropertyChangedFor(nameof(RemainingAfterUSD))]
    [NotifyPropertyChangedFor(nameof(RemainingAfterDisplay))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private string _amountUsdText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private PaymentMethodDto? _selectedMethod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private string _reference;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFifoAllocation))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private bool _isExplicitAllocation;

    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private CreditPaymentResultClientDto? _result;
    [ObservableProperty] private bool _dialogResult;

    public bool IsFifoAllocation => !IsExplicitAllocation;

    public decimal AmountUSD => CheckoutViewModel.ParseAmount(AmountUsdText);

    /// <summary>Proyección informativa a la tasa del día; el cobro real congela la tasa server-side.</summary>
    public decimal AmountBsS => TodayRate <= 0m
        ? 0m
        : Math.Round(AmountUSD * TodayRate, 2, MidpointRounding.AwayFromZero);

    public decimal SelectedTotalUSD => OpenReceivables
        .Where(option => option.IsSelected)
        .Sum(option => option.Receivable.BalanceUSD);

    /// <summary>Saldo resultante del cliente tras aplicar el monto capturado (USD).</summary>
    public decimal RemainingAfterUSD => Math.Max(0m, TotalOpenUSD - AmountUSD);

    public string RemainingAfterDisplay => $"Saldo resultante: {CurrencyDisplay.FormatPrimary(RemainingAfterUSD)}";

    public string? ValidationMessage
    {
        get
        {
            if (AmountUSD <= 0m) return "Ingrese un monto de abono mayor a cero.";
            if (SelectedMethod == null) return "Seleccione el método de pago del abono.";
            if (SelectedMethod.RequiresReference && string.IsNullOrWhiteSpace(Reference))
                return "El método de pago seleccionado exige número de referencia.";
            if (IsExplicitAllocation && !OpenReceivables.Any(option => option.IsSelected))
                return "Seleccione al menos una factura para imputar el abono.";
            if (IsExplicitAllocation && AmountUSD > SelectedTotalUSD + 0.005m)
                return "El monto excede el saldo de las facturas seleccionadas.";
            if (!IsExplicitAllocation && AmountUSD > TotalOpenUSD + 0.005m)
                return "El monto excede el saldo pendiente del cliente.";
            return null;
        }
    }

    [RelayCommand]
    private void SelectFifoMode()
    {
        IsExplicitAllocation = false;
        foreach (var option in OpenReceivables)
        {
            option.IsSelected = false;
        }
    }

    [RelayCommand]
    private void SelectExplicitMode()
    {
        IsExplicitAllocation = true;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsProcessing) return;

        if (AmountUSD <= 0m)
        {
            _dialogService.ShowWarning("Abono CxC", "Ingrese un monto de abono mayor a cero.");
            return;
        }

        if (SelectedMethod == null)
        {
            _dialogService.ShowWarning("Abono CxC", "Seleccione el método de pago del abono.");
            return;
        }

        if (SelectedMethod.RequiresReference && string.IsNullOrWhiteSpace(Reference))
        {
            _dialogService.ShowWarning("Abono CxC", "El método de pago seleccionado exige número de referencia.");
            return;
        }

        List<int>? receivableIds = null;
        if (IsExplicitAllocation)
        {
            if (!OpenReceivables.Any(option => option.IsSelected))
            {
                _dialogService.ShowWarning("Abono CxC", "Seleccione al menos una factura para imputar el abono.");
                return;
            }

            if (AmountUSD > SelectedTotalUSD + 0.005m)
            {
                _dialogService.ShowWarning("Abono CxC", "El monto excede el saldo de las facturas seleccionadas.");
                return;
            }

            receivableIds = OpenReceivables
                .Where(option => option.IsSelected)
                .Select(option => option.Receivable.ReceivableId)
                .ToList();
        }
        else if (AmountUSD > TotalOpenUSD + 0.005m)
        {
            _dialogService.ShowWarning("Abono CxC", "El monto excede el saldo pendiente del cliente.");
            return;
        }

        IsProcessing = true;
        try
        {
            var request = new CreditPaymentRequestClientDto
            {
                CustomerId = CustomerId,
                AmountUSD = AmountUSD,
                PaymentMethodId = SelectedMethod.Id,
                ReferenceNumber = string.IsNullOrWhiteSpace(Reference) ? null : Reference.Trim(),
                ReceivableIds = receivableIds,
                IdempotencyKey = Guid.NewGuid().ToString("N")
            };

            Result = await _salesService.RegisterCreditPaymentAsync(request);
            DialogResult = true;
            _dialogService.CloseCurrentModal(Result);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Abono CxC", $"No se pudo registrar el abono: {ex.Message}");
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

    private void OnAllocationSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTotalUSD));
        OnPropertyChanged(nameof(ValidationMessage));
    }

    public void Dispose()
    {
    }
}

/// <summary>Fila seleccionable del modal de abono; conserva la obligación y su saldo USD.</summary>
public partial class CreditPaymentAllocationOption : ObservableObject
{
    private readonly Action? _selectionChanged;

    public CreditPaymentAllocationOption(CreditReceivableClientItemDto receivable, Action? selectionChanged = null)
    {
        Receivable = receivable ?? throw new ArgumentNullException(nameof(receivable));
        _selectionChanged = selectionChanged;
    }

    public CreditReceivableClientItemDto Receivable { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();

    public string InvoiceDisplay => Receivable.InvoiceDisplay;

    public decimal BalanceUSD => Receivable.BalanceUSD;
}

