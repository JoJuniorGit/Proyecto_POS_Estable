using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
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
    private decimal _availableCashLocal;

    [ObservableProperty]
    private decimal _requestedAmountBsS;

    [ObservableProperty]
    private decimal _exchangeRate = 1.0m;

    [ObservableProperty]
    private PaymentMethodDto? _selectedPaymentMethod;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private decimal? _commissionPercentage;

    public ObservableCollection<PaymentMethodDto> ElectronicPaymentMethods { get; } = new();

    public CashAdvanceRegisterViewModel(List<PaymentMethodDto> paymentMethods, decimal availableCashLocal, decimal exchangeRate = 1.0m, ICashDrawerService? cashDrawer = null)
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

        SelectedPaymentMethod = ElectronicPaymentMethods.FirstOrDefault();
        ValidateInputs();
    }

    public bool IsTransfer => SelectedPaymentMethod != null &&
        (SelectedPaymentMethod.Name.Contains("Transfer", StringComparison.OrdinalIgnoreCase) ||
         SelectedPaymentMethod.Name.Contains("Pago Móvil", StringComparison.OrdinalIgnoreCase) ||
         SelectedPaymentMethod.Name.Contains("Pago Movil", StringComparison.OrdinalIgnoreCase));

    public decimal? CommissionAmountBsS => CommissionPercentage is decimal percentage
        ? Math.Round(RequestedAmountBsS * (percentage / 100.0m), 2, MidpointRounding.AwayFromZero)
        : null;

    public decimal? TotalToChargeBsS => CommissionAmountBsS is decimal commission
        ? RequestedAmountBsS + commission
        : null;

    public decimal? TotalToChargeUSD => TotalToChargeBsS is decimal total && ExchangeRate > 0
        ? total / ExchangeRate
        : null;

    public bool CanConfirm => RequestedAmountBsS > 0 && RequestedAmountBsS <= AvailableCashLocal && SelectedPaymentMethod != null && CommissionPercentage > 0 && string.IsNullOrEmpty(ErrorMessage);

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

    private void NotifyCalculationsChanged()
    {
        OnPropertyChanged(nameof(IsTransfer));
        OnPropertyChanged(nameof(CommissionPercentage));
        OnPropertyChanged(nameof(CommissionAmountBsS));
        OnPropertyChanged(nameof(TotalToChargeBsS));
        OnPropertyChanged(nameof(TotalToChargeUSD));
        OnPropertyChanged(nameof(CanConfirm));
    }

    private void ValidateInputs()
    {
        if (RequestedAmountBsS <= 0)
        {
            ErrorMessage = "Ingrese un monto mayor a cero.";
        }
        else if (RequestedAmountBsS > AvailableCashLocal)
        {
            ErrorMessage = $"El monto supera el efectivo en caja ({AvailableCashLocal:N2} Bs.S).";
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
