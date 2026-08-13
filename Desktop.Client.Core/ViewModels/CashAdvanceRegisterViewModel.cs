using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Desktop.Client.ViewModels;

public partial class CashAdvanceRegisterViewModel : ObservableObject
{
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

    public ObservableCollection<PaymentMethodDto> ElectronicPaymentMethods { get; } = new();

    public CashAdvanceRegisterViewModel(List<PaymentMethodDto> paymentMethods, decimal availableCashLocal, decimal exchangeRate = 1.0m)
    {
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

    public decimal CommissionPercentage => IsTransfer ? 7.0m : 10.0m;

    public decimal CommissionAmountBsS => Math.Round(RequestedAmountBsS * (CommissionPercentage / 100.0m), 2, MidpointRounding.AwayFromZero);

    public decimal TotalToChargeBsS => RequestedAmountBsS + CommissionAmountBsS;

    public decimal TotalToChargeUSD => ExchangeRate > 0 ? TotalToChargeBsS / ExchangeRate : 0;

    public bool CanConfirm => RequestedAmountBsS > 0 && RequestedAmountBsS <= AvailableCashLocal && SelectedPaymentMethod != null && string.IsNullOrEmpty(ErrorMessage);

    partial void OnRequestedAmountBsSChanged(decimal value)
    {
        NotifyCalculationsChanged();
        ValidateInputs();
    }

    partial void OnSelectedPaymentMethodChanged(PaymentMethodDto? value)
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
