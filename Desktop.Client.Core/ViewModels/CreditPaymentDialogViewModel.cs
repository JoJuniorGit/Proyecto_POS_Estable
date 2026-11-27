using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Desktop.Client.Helpers;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// T4 (F3, S3/S6): modal de abono CxC. La deuda vive en USD, pero la captura por defecto es la
/// moneda local (Bs.S en Dual) y sólo cambia a USD cuando el método elegido opera en divisas.
/// La conversión hacia el servicio se hace en USD y el saldo exacto se liquida sin residuo de
/// redondeo. La imputación es FIFO de cartera o explícita por factura.
/// </summary>
public partial class CreditPaymentDialogViewModel : ObservableObject, IDisposable
{
    private const decimal AmountTolerance = 0.005m;

    private readonly ISalesService _salesService;
    private readonly IDialogService _dialogService;
    private string _inputCurrencyCode;

    public CreditPaymentDialogViewModel(
        ISalesService salesService,
        IDialogService dialogService,
        int customerId,
        string customerName,
        IEnumerable<CreditReceivableClientItemDto> openReceivables,
        IEnumerable<PaymentMethodDto> methods,
        decimal todayRate,
        decimal? suggestedAmountUSD = null,
        string? customerCedulaOrRif = null)
    {
        _salesService = salesService ?? throw new ArgumentNullException(nameof(salesService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        CustomerId = customerId;
        CustomerName = string.IsNullOrWhiteSpace(customerName) ? "Cliente" : customerName;
        CustomerCedulaOrRif = string.IsNullOrWhiteSpace(customerCedulaOrRif) ? string.Empty : customerCedulaOrRif.Trim();
        TodayRate = todayRate;

        AvailableMethods = new ObservableCollection<PaymentMethodDto>(methods ?? Enumerable.Empty<PaymentMethodDto>());
        OpenReceivables = new ObservableCollection<CreditPaymentAllocationOption>(
            (openReceivables ?? Enumerable.Empty<CreditReceivableClientItemDto>())
                .Select(row => new CreditPaymentAllocationOption(row, todayRate, OnAllocationSelectionChanged)));

        _inputCurrencyCode = DefaultInputCurrencyCode;
        decimal initialUsd = suggestedAmountUSD ?? TotalOpenUSD;
        _amountText = FormatInputAmount(initialUsd);
        _reference = string.Empty;
    }

    public int CustomerId { get; }

    public string CustomerName { get; }

    public string CustomerCedulaOrRif { get; }

    public decimal TodayRate { get; }

    public ObservableCollection<PaymentMethodDto> AvailableMethods { get; }

    public ObservableCollection<CreditPaymentAllocationOption> OpenReceivables { get; }

    public decimal TotalOpenUSD => OpenReceivables.Sum(option => option.Receivable.BalanceUSD);

    public bool IsDual => CurrencyDisplay.Mode == MonetaryMode.Dual;

    public string CustomerContextDisplay => string.IsNullOrWhiteSpace(CustomerCedulaOrRif)
        ? CustomerName
        : $"{CustomerName} · {CustomerCedulaOrRif}";

    public string RateContextDisplay => TodayRate <= 0m
        ? string.Empty
        : $"Tasa: 1 {DisplayCode(CurrencyDisplay.PrimaryCurrencyCode)} = {CurrencyDisplay.Number(TodayRate)} {DisplayCode(CurrencyDisplay.LocalCurrencyCode)}";

    [ObservableProperty]
    private string _amountText;

    [ObservableProperty]
    private PaymentMethodDto? _selectedMethod;

    [ObservableProperty]
    private string _reference;

    [ObservableProperty]
    private bool _isExplicitAllocation;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private CreditPaymentResultClientDto? _result;

    [ObservableProperty]
    private bool _dialogResult;

    public bool IsFifoAllocation => !IsExplicitAllocation;

    /// <summary>Moneda de captura activa; USD sólo cuando el método opera en divisas.</summary>
    public string InputCurrencyCode => _inputCurrencyCode;

    public bool IsInputUsd => string.Equals(_inputCurrencyCode, "USD", StringComparison.OrdinalIgnoreCase);

    /// <summary>Prefijo interno del campo Monto: "Bs.S" por defecto, "USD" para métodos USD.</summary>
    public string InputCurrencyLabel => IsInputUsd ? "USD" : DisplayCode(_inputCurrencyCode);

    public string ReferencePlaceholder => SelectedMethod?.RequiresReference == true
        ? "N° de referencia / comprobante (obligatorio)"
        : "Referencia / comprobante (opcional)";

    public bool ShowReferencePlaceholder => string.IsNullOrWhiteSpace(Reference);

    public string AllocationHint => IsFifoAllocation
        ? "Se aplicará de la factura más antigua a la más reciente."
        : "Seleccione la factura específica a la cual aplicar el abono.";

    /// <summary>Saldo pendiente que la imputación activa puede cubrir (cartera o selección).</summary>
    public decimal DueUSD => IsExplicitAllocation ? SelectedTotalUSD : TotalOpenUSD;

    /// <summary>Valor USD que viaja al servicio; el monto local exacto se liquida sin residuo.</summary>
    public decimal AmountUSD
    {
        get
        {
            decimal typed = ParseAmount(AmountText);
            if (typed <= 0m)
            {
                return 0m;
            }

            decimal due = DueUSD;
            if (IsInputUsd)
            {
                return due > 0m && Math.Abs(typed - due) <= AmountTolerance ? due : typed;
            }

            if (TodayRate <= 0m)
            {
                return 0m;
            }

            decimal dueLocal = PricingHelper.ToBsS(due, TodayRate);
            if (due > 0m && Math.Abs(typed - dueLocal) <= AmountTolerance)
            {
                return due;
            }

            return PricingHelper.ToUSD(typed, TodayRate);
        }
    }

    /// <summary>Equivalente local del abono a la tasa del día (proyección informativa).</summary>
    public decimal AmountBsS => TodayRate <= 0m ? 0m : PricingHelper.ToBsS(AmountUSD, TodayRate);

    public decimal SelectedTotalUSD => OpenReceivables
        .Where(option => option.IsSelected)
        .Sum(option => option.Receivable.BalanceUSD);

    /// <summary>Saldo resultante del cliente tras aplicar el monto capturado (USD).</summary>
    public decimal RemainingAfterUSD => Math.Max(0m, TotalOpenUSD - AmountUSD);

    public bool HasPositiveAmount => AmountUSD > 0m;

    public bool IsRemainingZero => AmountUSD > 0m && Math.Abs(AmountUSD - TotalOpenUSD) <= AmountTolerance;

    public string PendingLocalDisplay => FormatLocalFromUsd(TotalOpenUSD);

    public string PendingUsdDisplay => CurrencyDisplay.FormatPrimary(TotalOpenUSD);

    public string PaymentLocalDisplay => FormatLocalFromUsd(AmountUSD);

    public string PaymentUsdDisplay => CurrencyDisplay.FormatPrimary(AmountUSD);

    public string RemainingLocalDisplay => FormatLocalFromUsd(RemainingAfterUSD);

    public string RemainingUsdDisplay => CurrencyDisplay.FormatPrimary(RemainingAfterUSD);

    /// <summary>Conversión en vivo de la tarjeta de captura, según la moneda del método.</summary>
    public string LiveConversionDisplay => IsInputUsd
        ? $"≈ {FormatLocalFromUsd(ParseAmount(AmountText))}"
        : $"≈ {CurrencyDisplay.FormatPrimary(AmountUSD)}";

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
            if (IsExplicitAllocation && ExceedsInputBalance(SelectedTotalUSD))
                return "El monto excede el saldo de las facturas seleccionadas.";
            if (!IsExplicitAllocation && ExceedsInputBalance(TotalOpenUSD))
                return "El monto excede el saldo pendiente del cliente.";
            return null;
        }
    }

    public bool CanConfirm => !IsProcessing && ValidationMessage == null;

    partial void OnAmountTextChanged(string value) => NotifyDerived();

    partial void OnSelectedMethodChanged(PaymentMethodDto? value)
    {
        string next = ResolveInputCurrency(value);
        if (!string.Equals(next, _inputCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            decimal currentUsd = ConvertTypedToUsd(ParseAmount(AmountText), _inputCurrencyCode);
            _inputCurrencyCode = next;
            _amountText = FormatInputAmount(currentUsd);
            OnPropertyChanged(nameof(AmountText));
        }

        NotifyDerived();
    }

    partial void OnReferenceChanged(string value)
    {
        OnPropertyChanged(nameof(ShowReferencePlaceholder));
        NotifyDerived();
    }

    partial void OnIsExplicitAllocationChanged(bool value) => NotifyDerived();

    partial void OnIsProcessingChanged(bool value) => NotifyDerived();

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

            if (ExceedsInputBalance(SelectedTotalUSD))
            {
                _dialogService.ShowWarning("Abono CxC", "El monto excede el saldo de las facturas seleccionadas.");
                return;
            }

            receivableIds = OpenReceivables
                .Where(option => option.IsSelected)
                .Select(option => option.Receivable.ReceivableId)
                .ToList();
        }
        else if (ExceedsInputBalance(TotalOpenUSD))
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

    private void OnAllocationSelectionChanged() => NotifyDerived();

    private bool ExceedsInputBalance(decimal dueUsd)
    {
        decimal typed = ParseAmount(AmountText);
        if (typed <= 0m) return false;

        if (IsInputUsd)
        {
            return typed > dueUsd + AmountTolerance;
        }

        return TodayRate > 0m && typed > PricingHelper.ToBsS(dueUsd, TodayRate) + AmountTolerance;
    }

    private string DefaultInputCurrencyCode
    {
        get
        {
            string code = NormalizeCurrencyCode(
                CurrencyDisplay.IsSingle ? CurrencyDisplay.PrimaryCurrencyCode : CurrencyDisplay.LocalCurrencyCode);
            if (string.IsNullOrWhiteSpace(code))
            {
                code = "USD";
            }

            return !string.Equals(code, "USD", StringComparison.OrdinalIgnoreCase) && TodayRate <= 0m
                ? "USD"
                : code;
        }
    }

    private string ResolveInputCurrency(PaymentMethodDto? method)
    {
        string code = NormalizeCurrencyCode(method?.Currency);
        if (string.Equals(code, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return "USD";
        }

        if (!string.IsNullOrWhiteSpace(code) && TodayRate > 0m)
        {
            return code;
        }

        return DefaultInputCurrencyCode;
    }

    private string FormatInputAmount(decimal usd) => CurrencyDisplay.Number(ConvertUsdToInput(usd, _inputCurrencyCode));

    private decimal ConvertUsdToInput(decimal usd, string currencyCode) =>
        string.Equals(currencyCode, "USD", StringComparison.OrdinalIgnoreCase) || TodayRate <= 0m
            ? usd
            : PricingHelper.ToBsS(usd, TodayRate);

    private decimal ConvertTypedToUsd(decimal typed, string currencyCode) =>
        string.Equals(currencyCode, "USD", StringComparison.OrdinalIgnoreCase)
            ? typed
            : TodayRate > 0m ? PricingHelper.ToUSD(typed, TodayRate) : 0m;

    private string FormatLocalFromUsd(decimal usd)
    {
        if (IsDual)
        {
            return CurrencyDisplay.FormatSecondary(PricingHelper.ToBsS(usd, TodayRate));
        }

        decimal primaryValue = string.Equals(CurrencyDisplay.PrimaryCurrencyCode, "USD", StringComparison.OrdinalIgnoreCase)
            ? usd
            : PricingHelper.ToBsS(usd, TodayRate);
        return CurrencyDisplay.FormatPrimary(primaryValue);
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(InputCurrencyCode));
        OnPropertyChanged(nameof(IsInputUsd));
        OnPropertyChanged(nameof(InputCurrencyLabel));
        OnPropertyChanged(nameof(ReferencePlaceholder));
        OnPropertyChanged(nameof(AllocationHint));
        OnPropertyChanged(nameof(DueUSD));
        OnPropertyChanged(nameof(AmountUSD));
        OnPropertyChanged(nameof(AmountBsS));
        OnPropertyChanged(nameof(SelectedTotalUSD));
        OnPropertyChanged(nameof(RemainingAfterUSD));
        OnPropertyChanged(nameof(HasPositiveAmount));
        OnPropertyChanged(nameof(IsRemainingZero));
        OnPropertyChanged(nameof(PendingLocalDisplay));
        OnPropertyChanged(nameof(PendingUsdDisplay));
        OnPropertyChanged(nameof(PaymentLocalDisplay));
        OnPropertyChanged(nameof(PaymentUsdDisplay));
        OnPropertyChanged(nameof(RemainingLocalDisplay));
        OnPropertyChanged(nameof(RemainingUsdDisplay));
        OnPropertyChanged(nameof(LiveConversionDisplay));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(CanConfirm));
    }

    private static string NormalizeCurrencyCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Trim().ToUpperInvariant();
        return normalized is "VES" or "BS.S" or "BSS" ? "VES" : normalized;
    }

    private static string DisplayCode(string? code) =>
        string.Equals(code, "VES", StringComparison.OrdinalIgnoreCase) ? "Bs.S" : code ?? string.Empty;

    /// <summary>Acepta el formato configurado del POS y capturas con punto decimal.</summary>
    private static decimal ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0m;
        }

        string clean = text.Trim();
        int lastComma = clean.LastIndexOf(',');
        int lastDot = clean.LastIndexOf('.');

        if (lastComma >= 0 && lastDot >= 0)
        {
            clean = lastComma > lastDot
                ? clean.Replace(".", string.Empty).Replace(',', '.')
                : clean.Replace(",", string.Empty);
        }
        else if (lastComma >= 0)
        {
            clean = clean.Replace(".", string.Empty).Replace(',', '.');
        }

        return decimal.TryParse(clean, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out decimal value)
            ? value
            : 0m;
    }

    public void Dispose()
    {
    }
}

/// <summary>Fila seleccionable del modal de abono; conserva la obligación y su saldo USD.</summary>
public partial class CreditPaymentAllocationOption : ObservableObject
{
    private readonly Action? _selectionChanged;
    private readonly decimal _todayRate;

    public CreditPaymentAllocationOption(
        CreditReceivableClientItemDto receivable,
        decimal todayRate,
        Action? selectionChanged = null)
    {
        Receivable = receivable ?? throw new ArgumentNullException(nameof(receivable));
        _todayRate = todayRate;
        _selectionChanged = selectionChanged;
    }

    public CreditReceivableClientItemDto Receivable { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();

    public string InvoiceDisplay => Receivable.InvoiceDisplay;

    public string StatusDisplay => Receivable.StatusDisplay;

    public decimal BalanceUSD => Receivable.BalanceUSD;

    public string BalanceLocalDisplay => CurrencyDisplay.Mode == MonetaryMode.Dual
        ? CurrencyDisplay.FormatSecondary(PricingHelper.ToBsS(BalanceUSD, _todayRate))
        : CurrencyDisplay.FormatPrimary(PrimaryCurrencyCodeValue);

    public string BalanceUsdDisplay => CurrencyDisplay.FormatPrimary(BalanceUSD);

    private decimal PrimaryCurrencyCodeValue =>
        string.Equals(CurrencyDisplay.PrimaryCurrencyCode, "USD", StringComparison.OrdinalIgnoreCase)
            ? BalanceUSD
            : PricingHelper.ToBsS(BalanceUSD, _todayRate);
}
