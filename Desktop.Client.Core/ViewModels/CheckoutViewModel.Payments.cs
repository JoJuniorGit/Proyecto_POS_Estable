using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Desktop.Client.Helpers;
using Desktop.Client.Services;
using System.Globalization;
using System.Linq;
using System.Windows;
using SalePaymentDto = Desktop.Client.Services.SalePaymentDto;

namespace Desktop.Client.ViewModels;

public partial class CheckoutViewModel
{
    [RelayCommand]
    private void AddPayment()
    {
        if (SelectedMethod == null)
        {
            ShowWarning("Método Requerido", "Por favor seleccione un método de pago antes de agregar el pago.");
            return;
        }

        var amountBsS = ParseAmount(AmountBsSText);
        if (amountBsS <= 0) return;
        if (CurrentExchangeRate <= 0) return;

        // Apply specialized rounding to the input based on payment type
        if (SelectedMethod.IsCash)
        {
            amountBsS = PricingHelper.RoundToCash(amountBsS);
        }
        else
        {
            amountBsS = PricingHelper.RoundToDigital(amountBsS);
        }

        var amountUsd = System.Math.Round(amountBsS / CurrentExchangeRate, 2, System.MidpointRounding.AwayFromZero);

        // Validation: prevent overpayment (with small tolerance)
        if (amountUsd > RemainingBalanceUsd + 0.05m) 
        {
            ShowWarning("Monto Excedido", "El monto ingresado excede el saldo restante de la venta.");
            return;
        }

        // Auto-clamp if almost finished to ensure precise zeroing
        if (amountUsd > RemainingBalanceUsd)
            amountUsd = RemainingBalanceUsd;

        if (SelectedMethod.RequiresReference && string.IsNullOrWhiteSpace(CurrentReference))
        {
            ShowWarning("Referencia Requerida", $"El método de pago '{SelectedMethod.Name}' requiere ingresar un número de referencia.");
            return;
        }

        var dto = new SalePaymentDto(SelectedMethod.Id, amountUsd, amountBsS, CurrentReference);
        Payments.Add(new CheckoutPaymentItem(dto, SelectedMethod.Name, amountBsS, SelectedMethod.IsCash));

        CurrentReference = string.Empty;
        RecalculateBalances();
    }

    [RelayCommand]
    private void RemovePayment(CheckoutPaymentItem? item)
    {
        if (item == null) return;
        Payments.Remove(item);
        RecalculateBalances();
    }

    [RelayCommand]
    private void EditPayment(CheckoutPaymentItem? item)
    {
        if (item == null) return;

        SelectedMethod = AvailableMethods.FirstOrDefault(m => m.Id == item.Dto.PaymentMethodId);
        AmountBsSText = item.AmountBsS.ToString("N2", CultureInfo.InvariantCulture);
        CurrentReference = item.Dto.ReferenceNumber ?? string.Empty;

        Payments.Remove(item);
        RecalculateBalances();
        FocusAmountInput = true;
    }

    private void SetAmountToRemainingBalance()
    {
        decimal balance = RemainingBalanceLocal;
        if (SelectedMethod != null && SelectedMethod.IsCash)
        {
            balance = PricingHelper.RoundToCash(balance);
        }
        
        AmountBsSText = balance.ToString("N2", CultureInfo.InvariantCulture);
    }

    public static decimal ParseAmount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        string clean = text.Trim();

        if (clean.Contains(',') && !clean.Contains('.'))
        {
            clean = clean.Replace(',', '.');
        }

        if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var valInvariant))
            return valInvariant;

        if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.CurrentCulture, out var valCurrent))
            return valCurrent;

        return 0m;
    }

    private void ShowWarning(string title, string message)
    {
        if (_dialogService != null)
        {
            _dialogService.ShowWarning(title, message);
        }
        else if (Application.Current != null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowError(string title, string message)
    {
        if (_dialogService != null)
        {
            _dialogService.ShowError(title, message);
        }
        else if (Application.Current != null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class CheckoutPaymentItem
{
    public SalePaymentDto Dto { get; }
    public string MethodName { get; }
    public decimal AmountBsS { get; }
    public bool IsCash { get; }
    public decimal AmountUsd => Dto.Amount;

    public CheckoutPaymentItem(SalePaymentDto dto, string methodName, decimal amountBsS, bool isCash)
    {
        Dto = dto;
        MethodName = methodName;
        AmountBsS = amountBsS;
        IsCash = isCash;
    }
}
