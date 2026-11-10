using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.Helpers;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class CashAdvanceRegisterViewModel : ObservableObject
{
    private readonly ICashDrawerService? _cashDrawerService;
    private int _commissionRequestVersion;

    public Action? CloseAction { get; set; }
    public bool DialogResult { get; private set; }

    [ObservableProperty]
    private decimal? _availableCashLocal;

    [ObservableProperty]
    private decimal _requestedAmountBsS;

    [ObservableProperty]
    private decimal _exchangeRate = 1.0m;

    [ObservableProperty]
    private PaymentMethodDto? _selectedPaymentMethod;

    [ObservableProperty]
    private PhysicalMethodBalanceClientDto? _selectedDispensedCashMethod;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private decimal? _commissionPercentage;

    public ObservableCollection<PaymentMethodDto> ElectronicPaymentMethods { get; } = new();

    public ObservableCollection<PhysicalMethodBalanceClientDto> PhysicalCashMethods { get; } = new();

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasPhysicalCashMethods => PhysicalCashMethods.Count > 0;

    public CashAdvanceRegisterViewModel(
        List<PaymentMethodDto> paymentMethods,
        decimal? availableCashLocal,
        decimal exchangeRate = 1.0m,
        ICashDrawerService? cashDrawer = null,
        IReadOnlyList<PhysicalMethodBalanceClientDto>? physicalCashMethods = null)
    {
        _cashDrawerService = cashDrawer;
        AvailableCashLocal = availableCashLocal;
        ExchangeRate = exchangeRate;

        // Filter out physical Cash payment methods: advances are strictly paid via electronic channels
        var electronicMethods = paymentMethods
            .Where(pm => !pm.IsCash && !pm.Name.Equals("Efectivo", StringComparison.OrdinalIgnoreCase))
            .OrderBy(pm => pm.DisplayOrder)
            .ToList();

        foreach (var pm in electronicMethods)
        {
            ElectronicPaymentMethods.Add(pm);
        }

        if (physicalCashMethods is not null)
        {
            foreach (var method in physicalCashMethods.OrderBy(method => method.PaymentMethodId))
            {
                PhysicalCashMethods.Add(method);
            }
        }

        SelectedPaymentMethod = ElectronicPaymentMethods.FirstOrDefault();
        SelectedDispensedCashMethod = PhysicalCashMethods.Count == 1
            ? PhysicalCashMethods[0]
            : PhysicalCashMethods.FirstOrDefault(method => method.CanDispense);
        ValidateInputs();
    }

    public bool IsTransfer => SelectedPaymentMethod != null &&
        (SelectedPaymentMethod.Name.Contains("Transfer", StringComparison.OrdinalIgnoreCase) ||
         SelectedPaymentMethod.Name.Contains("Pago Móvil", StringComparison.OrdinalIgnoreCase) ||
         SelectedPaymentMethod.Name.Contains("Pago Movil", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 8.153 (SEC-06): representación neutra del efectivo disponible cuando no es visible para el
    /// operador (no-supervisores); con valor conserva el formato N0 que muestra el diálogo.
    /// </summary>
    public string AvailableCashDisplay => AvailableCashLocal is decimal available ? MoneyFormat.N0(available) : "—";

    public string SelectedDispensedMethodDisplay => SelectedDispensedCashMethod is { } method
        ? $"{method.Name} ({method.CurrencyCode})"
        : "-";

    public string DispensedAvailableDisplay => SelectedDispensedCashMethod is { } method
        ? FormatMethodAmount(method, method.CurrentBalance)
        : "-";

    public decimal? ProjectedBalance => SelectedDispensedCashMethod is { } method
        ? method.CurrentBalance - RequestedAmountBsS
        : null;

    public string RequestedAmountDisplay => SelectedDispensedCashMethod is { } method
        ? FormatMethodAmount(method, RequestedAmountBsS)
        : MoneyFormat.N2(RequestedAmountBsS);

    public string ProjectedBalanceDisplay => SelectedDispensedCashMethod is { } method
        ? $"Existencia actual: {FormatMethodAmount(method, method.CurrentBalance)} -> Existencia tras el avance: {FormatMethodAmount(method, ProjectedBalance ?? method.CurrentBalance)}"
        : string.Empty;

    public bool HasSufficientDispensedBalance => SelectedDispensedCashMethod is not { } method
        || RequestedAmountBsS <= method.CurrentBalance;

    private static string FormatMethodAmount(PhysicalMethodBalanceClientDto method, decimal amount)
    {
        var symbol = string.IsNullOrWhiteSpace(method.CurrencySymbol)
            ? CurrencyDisplay.SymbolFor(method.CurrencyCode)
            : method.CurrencySymbol;
        return string.IsNullOrWhiteSpace(symbol)
            ? MoneyFormat.N2(amount)
            : $"{symbol}{MoneyFormat.N2(amount)}";
    }

    public decimal? CommissionAmountBsS => CommissionPercentage is decimal percentage
        ? Math.Round(RequestedAmountBsS * (percentage / 100.0m), 2, MidpointRounding.AwayFromZero)
        : null;

    public decimal? TotalToChargeBsS => CommissionAmountBsS is decimal commission
        ? RequestedAmountBsS + commission
        : null;

    public decimal? TotalToChargeUSD => TotalToChargeBsS is decimal total && ExchangeRate > 0
        ? total / ExchangeRate
        : null;

    // 8.153 (SEC-06): el tope local se aplica solo cuando el efectivo disponible es visible
    // (supervisores); con null el rechazo definitivo proviene del servidor.
    public bool CanConfirm => RequestedAmountBsS > 0
        && (!HasPhysicalCashMethods || SelectedDispensedCashMethod != null)
        && HasSufficientDispensedBalance
        && (!HasPhysicalCashMethods
            ? (AvailableCashLocal is not decimal cap || RequestedAmountBsS <= cap)
            : true)
        && SelectedPaymentMethod != null
        && CommissionPercentage > 0
        && string.IsNullOrEmpty(ErrorMessage);

    public async Task RefreshCommissionAsync(CancellationToken cancellationToken = default)
    {
        var requestVersion = Interlocked.Increment(ref _commissionRequestVersion);
        var isTransfer = IsTransfer;

        if (_cashDrawerService == null)
        {
            ApplyCommissionPercentage(null, requestVersion);
            return;
        }

        try
        {
            var percentage = await _cashDrawerService.GetAdvanceCommissionAsync(isTransfer, cancellationToken);
            ApplyCommissionPercentage(percentage, requestVersion);
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[CashAdvanceRegister] No se pudo leer la comisión del servidor para el canal {(isTransfer ? "transferencia" : "efectivo")}: {ex.Message}");
            ApplyCommissionPercentage(null, requestVersion);
        }
    }

    private void ApplyCommissionPercentage(decimal? percentage, int requestVersion)
    {
        if (requestVersion != _commissionRequestVersion) return;

        CommissionPercentage = percentage > 0 ? percentage : null;
    }

    partial void OnRequestedAmountBsSChanged(decimal value)
    {
        NotifyCalculationsChanged();
        ValidateInputs();
    }

    partial void OnSelectedPaymentMethodChanged(PaymentMethodDto? value)
    {
        NotifyCalculationsChanged();
        ValidateInputs();
        RefreshCommissionAsync().SafeFireAndForget("CashAdvanceRegisterViewModel.CommissionRefresh");
    }

    partial void OnCommissionPercentageChanged(decimal? value)
    {
        NotifyCalculationsChanged();
    }

    partial void OnSelectedDispensedCashMethodChanged(PhysicalMethodBalanceClientDto? value)
    {
        NotifyCalculationsChanged();
        ValidateInputs();
    }

    private void NotifyCalculationsChanged()
    {
        OnPropertyChanged(nameof(IsTransfer));
        OnPropertyChanged(nameof(CommissionPercentage));
        OnPropertyChanged(nameof(CommissionAmountBsS));
        OnPropertyChanged(nameof(TotalToChargeBsS));
        OnPropertyChanged(nameof(TotalToChargeUSD));
        OnPropertyChanged(nameof(SelectedDispensedMethodDisplay));
        OnPropertyChanged(nameof(DispensedAvailableDisplay));
        OnPropertyChanged(nameof(ProjectedBalance));
        OnPropertyChanged(nameof(ProjectedBalanceDisplay));
        OnPropertyChanged(nameof(RequestedAmountDisplay));
        OnPropertyChanged(nameof(HasSufficientDispensedBalance));
        OnPropertyChanged(nameof(CanConfirm));
    }

    private void ValidateInputs()
    {
        if (RequestedAmountBsS <= 0)
        {
            ErrorMessage = "Ingrese un monto mayor a cero.";
        }
        else if (HasPhysicalCashMethods && SelectedDispensedCashMethod is null)
        {
            ErrorMessage = "Seleccione el método físico que entrega el efectivo.";
        }
        else if (SelectedDispensedCashMethod is { IsActive: false } inactiveMethod)
        {
            ErrorMessage = $"El método físico {inactiveMethod.Name} ({inactiveMethod.CurrencyCode}) no está activo para dispensar.";
        }
        else if (SelectedDispensedCashMethod is { } method && !HasSufficientDispensedBalance)
        {
            ErrorMessage = $"Existencia insuficiente en {method.Name} ({method.CurrencyCode}): disponible {FormatMethodAmount(method, method.CurrentBalance)}, requerido {FormatMethodAmount(method, RequestedAmountBsS)}.";
        }
        else if (!HasPhysicalCashMethods
            && AvailableCashLocal is decimal availableCap
            && RequestedAmountBsS > availableCap)
        {
            // Single has no secondary currency: the ISO code identifies the local drawer.
            // Dual keeps the configured local symbol to preserve the existing message shape.
            var currencyLabel = CurrencyDisplay.IsSingle
                ? CurrencyDisplay.LocalCurrencyCode
                : CurrencyDisplay.LocalCurrencySymbol;
            ErrorMessage = $"El monto supera el efectivo en caja ({MoneyFormat.N2(availableCap)} {currencyLabel}).";
        }
        else if (SelectedPaymentMethod == null)
        {
            ErrorMessage = "Seleccione un método de pago electrónico.";
        }
        else
        {
            ErrorMessage = string.Empty;
        }

        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanConfirm));
    }

    [RelayCommand]
    private void Confirm()
    {
        ValidateInputs();
        if (!CanConfirm) return;

        DialogResult = true;
        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        CloseAction?.Invoke();
    }
}
