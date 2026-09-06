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
    private readonly ISalesService _salesService;
    private readonly SaleDto _sale;
    private string? _currentIdempotencyKey;

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

    public string FinalizeSaleButtonLabel => (IsPendingPickup && IsCustodyAllowed)
        ? "COBRAR Y ENVIAR A RETIRO"
        : IsOverrideMode
            ? (IsFullLiquidation ? "LIQUIDAR CUENTA" : "REGISTRAR ABONO")
            : "COBRAR Y FINALIZAR";

    public string CheckoutTitle => IsOverrideMode
        ? $"Liquidar / Abonar — Pedido #{OverrideSale!.Id}"
        : "Checkout";

    private decimal _totalUsd;
    public decimal TotalUSD
    {
        get => _totalUsd;
        set
        {
            if (SetProperty(ref _totalUsd, value))
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

    private CheckoutPaymentItem? _selectedPayment;
    public CheckoutPaymentItem? SelectedPayment
    {
        get => _selectedPayment;
        set => SetProperty(ref _selectedPayment, value);
    }

    private PaymentMethodDto? _selectedMethod;
    public PaymentMethodDto? SelectedMethod
    {
        get => _selectedMethod;
        set
        {
            if (SetProperty(ref _selectedMethod, value))
            {
                // Refresh input rounding if method type changes (Cash vs Digital)
                SetAmountToRemainingBalance();
            }
        }
    }

    private string _amountBsSText = "0.00";
    public string AmountBsSText
    {
        get => _amountBsSText;
        set
        {
            if (SetProperty(ref _amountBsSText, value))
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
            var bsS = ParseAmount(AmountBsSText);
            return System.Math.Round(bsS / CurrentExchangeRate, 2, System.MidpointRounding.AwayFromZero);
        }
    }

    private string _currentReference = string.Empty;
    public string CurrentReference
    {
        get => _currentReference;
        set => SetProperty(ref _currentReference, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    private bool _focusAmountInput;
    public bool FocusAmountInput
    {
        get => _focusAmountInput;
        set => SetProperty(ref _focusAmountInput, value);
    }

    private readonly UserSession? _userSession;
    private readonly IDialogService? _dialogService;

    public CheckoutViewModel(
        SaleDto sale,
        ObservableCollection<PaymentMethodDto>? availableMethods = null,
        ISalesService? salesService = null,
        decimal currentExchangeRate = 0m,
        UserSession? userSession = null,
        SaleDto? overrideSale = null,
        IDialogService? dialogService = null)
    {
        _sale = sale;
        _salesService = salesService ?? throw new System.ArgumentNullException(nameof(salesService));
        _userSession = userSession;
        _dialogService = dialogService;
        CurrentExchangeRate = currentExchangeRate;
        AvailableMethods = availableMethods ?? new();
        OverrideSale = overrideSale;

        // In override mode, TotalUSD = remaining debt of the OnHold sale
        TotalUSD = IsOverrideMode ? OriginalRemainingDebtUsd : sale.TotalUSD;

        SelectedMethod = null;
        SetAmountToRemainingBalance();
        _currentIdempotencyKey = System.Guid.NewGuid().ToString();
        WeakReferenceMessenger.Default.Register(this);
    }

    public void Receive(CartUpdatedMessage message)
    {
        TotalUSD = message.NewTotal;
    }

    public void UpdateCustomer(int? customerId, string? customerName)
    {
        _sale.CustomerId = customerId;
        _sale.CustomerName = customerName;
        OnPropertyChanged(nameof(IsDefaultCustomer));
        OnPropertyChanged(nameof(IsCustodyAllowed));
        OnPropertyChanged(nameof(PendingPickupErrorMessage));
        OnPropertyChanged(nameof(CanFinalize));
        OnPropertyChanged(nameof(FinalizeSaleButtonLabel));
        OnPropertyChanged(nameof(ValidationHelperMessage));
        FinalizeSaleCommand.NotifyCanExecuteChanged();
    }

    private void RecalculateBalances()
    {
        OnPropertyChanged(nameof(PaidAmountUsd));
        OnPropertyChanged(nameof(PaidAmountLocal));
        OnPropertyChanged(nameof(RemainingBalanceUsd));
        OnPropertyChanged(nameof(RemainingBalanceLocal));
        OnPropertyChanged(nameof(RoundingAdjustment));
        OnPropertyChanged(nameof(HasValidPayments));
        OnPropertyChanged(nameof(IsFullLiquidation));
        OnPropertyChanged(nameof(IsCustodyAllowed));
        OnPropertyChanged(nameof(CanFinalize));
        OnPropertyChanged(nameof(FinalizeSaleButtonLabel));
        OnPropertyChanged(nameof(ValidationHelperMessage));
        
        SetAmountToRemainingBalance();
        FinalizeSaleCommand.NotifyCanExecuteChanged();
    }

    private bool _isPendingPickup;
    public bool IsPendingPickup
    {
        get => _isPendingPickup;
        set
        {
            if (SetProperty(ref _isPendingPickup, value))
            {
                OnPropertyChanged(nameof(PendingPickupErrorMessage));
                OnPropertyChanged(nameof(IsCustodyAllowed));
                OnPropertyChanged(nameof(CanFinalize));
                OnPropertyChanged(nameof(FinalizeSaleButtonLabel));
                OnPropertyChanged(nameof(ValidationHelperMessage));
                FinalizeSaleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasValidPayments => Payments.Any(p => p.AmountBsS > 0 || p.AmountUsd > 0);
    public bool IsFullLiquidation => HasValidPayments && RemainingBalanceUsd <= 0.05m;
    public bool IsDefaultCustomer => !_sale.CustomerId.HasValue 
        || string.IsNullOrWhiteSpace(_sale.CustomerName) 
        || _sale.CustomerName.ToLower().Contains("consumidor final") 
        || _sale.CustomerName.ToLower().Contains("general");
    public bool IsCustodyAllowed => IsFullLiquidation && !IsDefaultCustomer;

    public bool CanFinalize => HasValidPayments && 
        (IsOverrideMode ? true : IsFullLiquidation) && 
        (!IsPendingPickup || IsCustodyAllowed);

    public string? ValidationHelperMessage
    {
        get
        {
            if (!HasValidPayments)
                return "Agregue al menos un método de pago antes de continuar.";
            if (!IsOverrideMode && !IsFullLiquidation)
                return "El monto acumulado aún no cubre el 100% del total de la venta.";
            if (IsPendingPickup && !IsCustodyAllowed)
                return "Para registrar un apartado pagado (Mercancía en Custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono). Asigne un cliente a la venta antes de continuar.";
            return null;
        }
    }

    public string? PendingPickupErrorMessage
    {
        get
        {
            if (!IsPendingPickup) return null;
            if (IsDefaultCustomer)
            {
                return "Para registrar un apartado pagado (Mercancía en Custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono). Asigne un cliente a la venta antes de continuar.";
            }
            return null;
        }
    }

    private bool CanFinalizeSale() => CanFinalize;

    [RelayCommand(CanExecute = nameof(CanFinalizeSale))]
    private async Task FinalizeSale()
    {
        var targetSale = OverrideSale ?? _sale;

        if (IsPendingPickup && !string.IsNullOrEmpty(PendingPickupErrorMessage))
        {
            ShowWarning("Cliente Requerido", PendingPickupErrorMessage);
            return;
        }

        IsProcessing = true;
        try
        {
            var rawPayments = Payments.Select(p => new SalePaymentDto(p.Dto.PaymentMethodId, p.Dto.Amount, p.AmountBsS, p.Dto.ReferenceNumber));

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
                    int realId = await _salesService.CompleteSaleAsync(
                        targetSale.Id, CurrentExchangeRate, rawPayments,
                        RoundingAdjustment, _userSession?.CurrentUser?.Id, IsPendingPickup,
                        _currentIdempotencyKey);

                    _currentIdempotencyKey = null;
                    WeakReferenceMessenger.Default.Unregister<CartUpdatedMessage>(this);
                    // Pass result: positive id = liquidated, negative = abono
                    DialogHost.CloseDialogCommand.Execute(realId, null);
                }
                else
                {
                    // Partial abono: add payment to OnHold sale (keeps it pending)
                    foreach (var p in rawPayments)
                    {
                        await _salesService.AddPaymentToHoldSaleAsync(targetSale.Id, new AddPaymentRequestDto
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
                var paymentsList = Payments.Select(p => new SalePaymentDto(p.Dto.PaymentMethodId, p.Dto.Amount, p.AmountBsS, p.Dto.ReferenceNumber));
                int realId = await _salesService.CompleteSaleAsync(
                    _sale.Id, CurrentExchangeRate, paymentsList,
                    RoundingAdjustment, _userSession?.CurrentUser?.Id, IsPendingPickup,
                    _currentIdempotencyKey);

                _currentIdempotencyKey = null;
                WeakReferenceMessenger.Default.Unregister<CartUpdatedMessage>(this);
                DialogHost.CloseDialogCommand.Execute(realId, null);
            }
        }
        catch (System.Exception ex)
        {
            ShowError("Error al Procesar Cobro", $"Error al procesar el cobro: {ex.Message}. Verifique la conexión con el servidor e intente nuevamente.");
        }
        finally
        {
            IsProcessing = false;
        }
    }
}
