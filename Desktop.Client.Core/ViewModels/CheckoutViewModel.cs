using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using MaterialDesignThemes.Wpf;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Messages;
using Core.DTOs;
using Desktop.Client.Helpers;
using SalePaymentDto = Desktop.Client.Services.SalePaymentDto;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Manages the payment collection process, enforcing centralized rounding 
/// and ensuring accounting integrity with RoundingAdjustments.
/// </summary>
public partial class CheckoutViewModel : ObservableObject, IRecipient<CartUpdatedMessage>
{
    private readonly ISalesService _sales_service;
    private readonly SaleDto _sale;

    // ── Override Sale (for pending/OnHold sales) ──
    /// <summary>
    /// When set, checkout operates on an existing OnHold sale instead of the active cart sale.
    /// TotalUSD is recalculated as the remaining debt (TotalUSD - TotalPaidUSD).
    /// FinalizeSale will either fully complete or register a partial abono.
    /// </summary>
    public SaleDto? OverrideSale { get; }
    public bool IsOverrideMode => OverrideSale != null;

    /// <summary>
    /// The original remaining debt of the override sale before any new payments in this session.
    /// Only meaningful when IsOverrideMode is true.
    /// </summary>
    public decimal OriginalRemainingDebtUsd => IsOverrideMode
        ? System.Math.Max(0m, (OverrideSale!.RemainingBalanceUSD > 0m
            ? OverrideSale.RemainingBalanceUSD
            : OverrideSale.TotalUSD - OverrideSale.TotalPaidUSD))
        : TotalUSD;

    public string FinalizeSaleButtonLabel => IsOverrideMode ? "LIQUIDAR / ABONAR" : "FINALIZAR VENTA";
    public string CheckoutTitle => IsOverrideMode
        ? $"Liquidar / Abonar — Pedido #{OverrideSale!.Id}"
        : "Checkout";

    private decimal _total_usd;
    public decimal TotalUSD

    {
        get => _total_usd;
        set
        {
            if (SetProperty(ref _total_usd, value))
            {
                OnPropertyChanged(nameof(TotalAmountLocal));
                RecalculateBalances();
            }
        }
    }

    public decimal CurrentExchangeRate { get; }

    // ── Amounts in Bs.S (standardized via PricingHelper and persistent TotalBsS) ──
    public decimal TotalAmountLocal => IsOverrideMode
        ? System.Math.Max(0m, (OverrideSale!.TotalBsS > 0m
            ? OverrideSale.TotalBsS - OverrideSale.Payments.Sum(p => p.AmountBsS > 0m ? p.AmountBsS : (p.Amount * (p.ExchangeRate > 0m ? p.ExchangeRate : CurrentExchangeRate)))
            : PricingHelper.ToBsS(TotalUSD, CurrentExchangeRate)))
        : (_sale.TotalBsS > 0m ? _sale.TotalBsS : PricingHelper.ToBsS(TotalUSD, CurrentExchangeRate));
    public decimal SubtotalLocal => _sale.SubtotalBsS > 0m ? _sale.SubtotalBsS : PricingHelper.ToBsS(_sale.Subtotal, CurrentExchangeRate);

    // ── Paid ──
    public decimal PaidAmountUsd => Payments.Sum(p => p.AmountUsd);
    
    /// <summary>
    /// Sum of all payments received in local currency, respecting their individual rounding (Cash vs Digital).
    /// </summary>
    public decimal PaidAmountLocal => Payments.Sum(p => p.AmountBsS);

    // ── Remaining (Golden Rule: TotalLocal - PaidLocal) ──
    public decimal RemainingBalanceLocal => System.Math.Max(0, TotalAmountLocal - PaidAmountLocal);

    /// <summary>
    /// The USD equivalent of what's still owed, for display purposes.
    /// </summary>
    public decimal RemainingBalanceUsd => System.Math.Max(0, TotalUSD - PaidAmountUsd);

    /// <summary>
    /// Captures the cent-level difference required to zero-out the balance.
    /// Calculated when the sale is finalized.
    /// </summary>
    public decimal RoundingAdjustment => (RemainingBalanceUsd <= 0.01m) ? (PaidAmountLocal - TotalAmountLocal) : 0m;

    public ObservableCollection<CheckoutPaymentItem> Payments { get; } = new();
    public ObservableCollection<PaymentMethodDto> AvailableMethods { get; }

    private CheckoutPaymentItem? _selected_payment;
    public CheckoutPaymentItem? SelectedPayment
    {
        get => _selected_payment;
        set => SetProperty(ref _selected_payment, value);
    }

    private PaymentMethodDto? _selected_method;
    public PaymentMethodDto? SelectedMethod
    {
        get => _selected_method;
        set
        {
            if (SetProperty(ref _selected_method, value))
            {
                // Refresh input rounding if method type changes (Cash vs Digital)
                SetAmountToRemainingBalance();
            }
        }
    }

    private string _amount_bs_s_text = "0.00";
    public string AmountBsSText
    {
        get => _amount_bs_s_text;
        set
        {
            if (SetProperty(ref _amount_bs_s_text, value))
            {
                OnPropertyChanged(nameof(AmountUsdPreview));
            }
        }
    }

    public decimal AmountUsdPreview
    {
        get
        {
            if (CurrentExchangeRate <= 0) return 0;
            var bs_s = ParseAmount(AmountBsSText);
            return System.Math.Round(bs_s / CurrentExchangeRate, 2, System.MidpointRounding.AwayFromZero);
        }
    }

    private string _current_reference = string.Empty;
    public string CurrentReference
    {
        get => _current_reference;
        set => SetProperty(ref _current_reference, value);
    }

    private bool _is_processing;
    public bool IsProcessing
    {
        get => _is_processing;
        set => SetProperty(ref _is_processing, value);
    }

    private bool _focus_amount_input;
    public bool FocusAmountInput
    {
        get => _focus_amount_input;
        set => SetProperty(ref _focus_amount_input, value);
    }

    private readonly UserSession? _user_session;

    public CheckoutViewModel(
        SaleDto sale,
        ObservableCollection<PaymentMethodDto> available_methods,
        ISalesService sales_service,
        decimal current_exchange_rate,
        UserSession? user_session = null,
        SaleDto? override_sale = null)
    {
        _sale = sale;
        _sales_service = sales_service;
        _user_session = user_session;
        CurrentExchangeRate = current_exchange_rate;
        AvailableMethods = available_methods;
        OverrideSale = override_sale;

        // In override mode, TotalUSD = remaining debt of the OnHold sale
        TotalUSD = IsOverrideMode ? OriginalRemainingDebtUsd : sale.TotalUSD;

        SelectedMethod = null;
        SetAmountToRemainingBalance();
        WeakReferenceMessenger.Default.Register(this);
    }

    public void Receive(CartUpdatedMessage message)
    {
        TotalUSD = message.NewTotal;
    }

    [RelayCommand]
    private void AddPayment()
    {
        if (SelectedMethod == null)
        {
            MessageBox.Show("Por favor seleccione un método de pago antes de agregar el pago.", "Método Requerido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var amount_bs_s = ParseAmount(AmountBsSText);
        if (amount_bs_s <= 0) return;
        if (CurrentExchangeRate <= 0) return;

        // Apply specialized rounding to the input based on payment type
        if (SelectedMethod.IsCash)
        {
            amount_bs_s = PricingHelper.RoundToCash(amount_bs_s);
        }
        else
        {
            amount_bs_s = PricingHelper.RoundToDigital(amount_bs_s);
        }

        var amount_usd = System.Math.Round(amount_bs_s / CurrentExchangeRate, 2, System.MidpointRounding.AwayFromZero);

        // Validation: prevent overpayment (with small tolerance)
        if (amount_usd > RemainingBalanceUsd + 0.05m) 
        {
            MessageBox.Show("Cannot overpay the remaining balance.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Auto-clamp if almost finished to ensure precise zeroing
        if (amount_usd > RemainingBalanceUsd)
            amount_usd = RemainingBalanceUsd;

        if (SelectedMethod.RequiresReference && string.IsNullOrWhiteSpace(CurrentReference))
        {
            MessageBox.Show($"The selected payment method ({SelectedMethod.Name}) requires a Reference Number.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dto = new SalePaymentDto(SelectedMethod.Id, amount_usd, amount_bs_s, CurrentReference);
        Payments.Add(new CheckoutPaymentItem(dto, SelectedMethod.Name, amount_bs_s, SelectedMethod.IsCash));

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

    private void RecalculateBalances()
    {
        OnPropertyChanged(nameof(PaidAmountUsd));
        OnPropertyChanged(nameof(PaidAmountLocal));
        OnPropertyChanged(nameof(RemainingBalanceUsd));
        OnPropertyChanged(nameof(RemainingBalanceLocal));
        OnPropertyChanged(nameof(RoundingAdjustment));
        
        SetAmountToRemainingBalance();
        FinalizeSaleCommand.NotifyCanExecuteChanged();
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

    private bool _is_pending_pickup;
    public bool IsPendingPickup
    {
        get => _is_pending_pickup;
        set
        {
            if (SetProperty(ref _is_pending_pickup, value))
            {
                OnPropertyChanged(nameof(PendingPickupErrorMessage));
            }
        }
    }

    public string? PendingPickupErrorMessage
    {
        get
        {
            if (!IsPendingPickup) return null;
            var custName = (_sale.CustomerName ?? string.Empty).ToLower();
            if (!_sale.CustomerId.HasValue || custName.Contains("consumidor final") || custName.Contains("general"))
            {
                return "Para registrar un apartado pagado (Mercancía en Custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono). Asigne un cliente a la venta antes de continuar.";
            }
            return null;
        }
    }

    /// In override mode: allow proceeding with any payment amount (partial or full abono).
    /// In normal mode: require balance fully settled.
    private bool CanFinalizeSale() => IsOverrideMode
        ? Payments.Any()
        : (RemainingBalanceUsd <= 0.05m && Payments.Any());

    [RelayCommand(CanExecute = nameof(CanFinalizeSale))]
    private async Task FinalizeSale()
    {
        var targetSale = OverrideSale ?? _sale;

        if (IsPendingPickup && !string.IsNullOrEmpty(PendingPickupErrorMessage))
        {
            MessageBox.Show(PendingPickupErrorMessage, "Cliente Requerido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsProcessing = true;
        try
        {
            var raw_payments = Payments.Select(p => new SalePaymentDto(p.Dto.PaymentMethodId, p.Dto.Amount, p.AmountBsS, p.Dto.ReferenceNumber));

            if (IsOverrideMode)
            {
                // Determine whether this covers the full remaining debt or is a partial abono.
                // PaidAmountUsd = sum of payments added this session.
                // OriginalRemainingDebtUsd = debt before this checkout session.
                decimal debtAfterPayment = OriginalRemainingDebtUsd - PaidAmountUsd;
                bool isFullyLiquidated = debtAfterPayment <= 0.05m;

                if (isFullyLiquidated)
                {
                    // Full liquidation: mark sale as Completed
                    targetSale.RoundingAdjustment = RoundingAdjustment;
                    int real_id = await _sales_service.CompleteSaleAsync(
                        targetSale.Id, CurrentExchangeRate, raw_payments,
                        RoundingAdjustment, _user_session?.CurrentUser?.Id, IsPendingPickup);

                    WeakReferenceMessenger.Default.Unregister<CartUpdatedMessage>(this);
                    // Pass result: positive id = liquidated, negative = abono
                    DialogHost.CloseDialogCommand.Execute(real_id, null);
                }
                else
                {
                    // Partial abono: add payment to OnHold sale (keeps it pending)
                    foreach (var p in raw_payments)
                    {
                        await _sales_service.AddPaymentToHoldSaleAsync(targetSale.Id, new AddPaymentRequestDto
                        {
                            PaymentMethodId = p.PaymentMethodId,
                            AmountBsS = p.AmountBsS,
                            AmountUSD = p.Amount,
                            ExchangeRate = CurrentExchangeRate,
                            ReferenceNumber = p.ReferenceNumber
                        });
                    }

                    WeakReferenceMessenger.Default.Unregister<CartUpdatedMessage>(this);
                    // Pass -1 to indicate abono (not a full sale completion)
                    DialogHost.CloseDialogCommand.Execute(-1, null);
                }
            }
            else
            {
                // Normal checkout mode: complete the active cart sale
                _sale.RoundingAdjustment = RoundingAdjustment;
                var payments_list = Payments.Select(p => new SalePaymentDto(p.Dto.PaymentMethodId, p.Dto.Amount, p.AmountBsS, p.Dto.ReferenceNumber));
                int real_id = await _sales_service.CompleteSaleAsync(
                    _sale.Id, CurrentExchangeRate, payments_list,
                    RoundingAdjustment, _user_session?.CurrentUser?.Id, IsPendingPickup);

                WeakReferenceMessenger.Default.Unregister<CartUpdatedMessage>(this);
                DialogHost.CloseDialogCommand.Execute(real_id, null);
            }
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Error al procesar: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
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
