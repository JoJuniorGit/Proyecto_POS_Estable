using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Helpers;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SalePaymentDto = Desktop.Client.Services.SalePaymentDto;

namespace Desktop.Client.ViewModels;

public partial class CheckoutViewModel : ObservableObject, IRecipient<CartUpdatedMessage>, System.IDisposable
{
    private readonly ISalesService _salesService;
    private readonly SaleDto _sale;
    private string? _currentIdempotencyKey;

    // 8.156 (CLEAN-05): preview canónico del servidor (fuente de verdad del gate de cobro).
    private CheckoutPreviewClientDto? _preview;
    private string? _previewSignature;
    private bool _previewFailed;
    private int _previewVersion;

    /// <summary>Seam determinista de tests: última ejecución del fetch del preview canónico.</summary>
    public Task? PendingPreview { get; private set; }

    public SaleDto? OverrideSale { get; }
    public bool IsOverrideMode => OverrideSale != null;

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

    public decimal TotalAmountLocal => IsOverrideMode
        ? System.Math.Max(0m, (OverrideSale!.TotalBsS > 0m
            ? OverrideSale.TotalBsS - OverrideSale.Payments.Sum(p => p.AmountBsS > 0m ? p.AmountBsS : (p.Amount * (p.ExchangeRate > 0m ? p.ExchangeRate : CurrentExchangeRate)))
            : PricingHelper.ToBsS(TotalUSD, CurrentExchangeRate)))
        : (_sale.TotalBsS > 0m ? _sale.TotalBsS : PricingHelper.ToBsS(TotalUSD, CurrentExchangeRate));
    public decimal SubtotalLocal => _sale.SubtotalBsS > 0m ? _sale.SubtotalBsS : PricingHelper.ToBsS(_sale.Subtotal, CurrentExchangeRate);

    public decimal PaidAmountUsd => Payments.Sum(p => p.AmountUsd);
    
    public decimal PaidAmountLocal => Payments.Sum(p => p.AmountBsS);

    // 8.156 (CLEAN-05): con preview fresco el saldo mostrado es el del servidor; sin preview
    // se conserva el cálculo local SOLO como respaldo de presentación (el gate ya está cerrado).
    public decimal RemainingBalanceLocal => IsPreviewFresh
        ? _preview!.RemainingBalanceBsS
        : System.Math.Max(0, TotalAmountLocal - PaidAmountLocal);

    public decimal RemainingBalanceUsd => IsPreviewFresh
        ? _preview!.RemainingBalanceUSD
        : System.Math.Max(0, TotalUSD - PaidAmountUsd);

    public decimal RoundingAdjustment => IsPreviewFresh ? _preview!.RoundingAdjustment : 0m;

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

    private System.Threading.CancellationTokenSource? _cts;

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    [RelayCommand]
    private void CancelCheckout()
    {
        _cts?.Cancel();
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

        // In override mode, TotalUSD = remaining debt of the OnHold sale.
        // Se asigna el backing field para que el ctor dispare UN solo fetch del preview
        // (el setter de TotalUSD invoca RecalculateBalances, que también lo dispararía).
        _totalUsd = IsOverrideMode ? OriginalRemainingDebtUsd : sale.TotalUSD;

        SelectedMethod = null;
        SetAmountToRemainingBalance();
        _currentIdempotencyKey = System.Guid.NewGuid().ToString();
        WeakReferenceMessenger.Default.Register(this);

        // Paridad con el web: el modal abre con la primera validación canónica ya disparada.
        TriggerPreviewFetch();
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
        OnPropertyChanged(nameof(IsPreviewFresh));
        OnPropertyChanged(nameof(IsFullLiquidation));
        OnPropertyChanged(nameof(IsCustodyAllowed));
        OnPropertyChanged(nameof(CanFinalize));
        OnPropertyChanged(nameof(FinalizeSaleButtonLabel));
        OnPropertyChanged(nameof(ValidationHelperMessage));

        SetAmountToRemainingBalance();
        TriggerPreviewFetch();
        FinalizeSaleCommand.NotifyCanExecuteChanged();
    }

    private static readonly System.Text.Json.JsonSerializerOptions PreviewSignatureJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private decimal ResolvePreviewRate()
    {
        if (CurrentExchangeRate > 0m) return CurrentExchangeRate;
        var targetSale = OverrideSale ?? _sale;
        return targetSale.AppliedRate > 0m ? targetSale.AppliedRate : 1m;
    }

    /// <summary>Espejo de `buildCheckoutPreviewRequest` del web: en override se anteponen los pagos
    /// persistidos (paymentMethodId/amount/amountBsS) y luego van los pagos actuales.</summary>
    private List<SalePaymentDto> BuildPreviewPayments()
    {
        var payments = new List<SalePaymentDto>();
        if (IsOverrideMode)
        {
            payments.AddRange(OverrideSale!.Payments.Select(p => new SalePaymentDto(p.PaymentMethodId, p.Amount, p.AmountBsS, null)));
        }

        payments.AddRange(Payments.Select(p => new SalePaymentDto(p.Dto.PaymentMethodId, p.Dto.Amount, p.AmountBsS, p.Dto.ReferenceNumber)));
        return payments;
    }

    private static string BuildPreviewSignature(int saleId, decimal exchangeRate, IReadOnlyList<SalePaymentDto> payments)
    {
        // Mismo JSON que viaja en el POST (Web defaults => camelCase): cualquier cambio de venta,
        // tasa o pagos invalida la firma y cierra el gate hasta la próxima respuesta.
        var payload = new { SaleId = saleId, ExchangeRate = exchangeRate, Payments = payments };
        return System.Text.Json.JsonSerializer.Serialize(payload, PreviewSignatureJsonOptions);
    }

    private void TriggerPreviewFetch()
    {
        var targetSale = OverrideSale ?? _sale;
        if (targetSale.Id <= 0)
        {
            _preview = null;
            _previewSignature = null;
            _previewFailed = false;
            PendingPreview = Task.CompletedTask;
            NotifyPreviewDependentProperties();
            return;
        }

        int version = ++_previewVersion;
        decimal rate = ResolvePreviewRate();
        var payments = BuildPreviewPayments();
        string signature = BuildPreviewSignature(targetSale.Id, rate, payments);
        PendingPreview = FetchPreviewAsync(targetSale.Id, rate, payments, version, signature);
    }

    private async Task FetchPreviewAsync(int saleId, decimal rate, IReadOnlyList<SalePaymentDto> payments, int version, string signature)
    {
        try
        {
            var preview = await _salesService.GetCheckoutPreviewAsync(saleId, rate, payments);

            // Una respuesta vieja (ya hay un fetch más nuevo) no puede reabrir el gate.
            if (version != _previewVersion) return;

            if (preview == null)
            {
                _preview = null;
                _previewSignature = null;
                _previewFailed = true;
            }
            else
            {
                _preview = preview;
                _previewSignature = signature;
                _previewFailed = false;
            }
        }
        catch
        {
            // Fail-closed: el fallo de la última validación cierra el gate (mensaje canónico del web).
            if (version != _previewVersion) return;
            _preview = null;
            _previewSignature = null;
            _previewFailed = true;
        }

        NotifyPreviewDependentProperties();
    }

    private void NotifyPreviewDependentProperties()
    {
        OnPropertyChanged(nameof(IsPreviewFresh));
        OnPropertyChanged(nameof(IsFullLiquidation));
        OnPropertyChanged(nameof(IsCustodyAllowed));
        OnPropertyChanged(nameof(CanFinalize));
        OnPropertyChanged(nameof(RoundingAdjustment));
        OnPropertyChanged(nameof(RemainingBalanceUsd));
        OnPropertyChanged(nameof(RemainingBalanceLocal));
        OnPropertyChanged(nameof(FinalizeSaleButtonLabel));
        OnPropertyChanged(nameof(ValidationHelperMessage));
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

    /// <summary>8.156 (CLEAN-05): espejo de `isPreviewFresh` del web — la respuesta aplicada debe
    /// existir, no haber fallado y su firma (venta|tasa|pagos) debe coincidir con el estado vigente.</summary>
    public bool IsPreviewFresh
    {
        get
        {
            if (_preview == null || _previewFailed || _previewSignature == null) return false;
            var targetSale = OverrideSale ?? _sale;
            var currentSignature = BuildPreviewSignature(targetSale.Id, ResolvePreviewRate(), BuildPreviewPayments());
            return string.Equals(_previewSignature, currentSignature, System.StringComparison.Ordinal);
        }
    }

    // Ya no hay umbral local 0.05: la liquidación completa la define el servidor.
    public bool IsFullLiquidation => IsPreviewFresh && _preview!.IsFullyPaid;
    public bool IsDefaultCustomer => !_sale.CustomerId.HasValue 
        || string.IsNullOrWhiteSpace(_sale.CustomerName) 
        || _sale.CustomerName.ToLower().Contains("consumidor final") 
        || _sale.CustomerName.ToLower().Contains("general");
    public bool IsCustodyAllowed => IsFullLiquidation && !IsDefaultCustomer;

    public bool CanFinalize => HasValidPayments && 
        IsPreviewFresh &&
        (IsOverrideMode ? true : IsFullLiquidation) && 
        (!IsPendingPickup || IsCustodyAllowed);

    public string? ValidationHelperMessage
    {
        get
        {
            if (!HasValidPayments)
                return "Agregue al menos un método de pago antes de continuar.";
            // Misma prioridad y texto exacto del web cuando falla la validación canónica.
            if (_previewFailed)
                return "No se pudo validar el cobro con el servidor. Verifique la conexión e intente nuevamente (la transacción no puede cerrarse sin la validación canónica).";
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
        // 8.156 (CLEAN-05): sin validación canónica fresca no se cierra la transacción.
        if (!CanFinalize) return;

        var targetSale = OverrideSale ?? _sale;

        if (IsPendingPickup && !string.IsNullOrEmpty(PendingPickupErrorMessage))
        {
            ShowWarning("Cliente Requerido", PendingPickupErrorMessage);
            return;
        }

        IsProcessing = true;
        _cts = new System.Threading.CancellationTokenSource();
        try
        {
            var rawPayments = Payments.Select(p => new SalePaymentDto(p.Dto.PaymentMethodId, p.Dto.Amount, p.AmountBsS, p.Dto.ReferenceNumber));

            if (IsOverrideMode)
            {
                // 8.156 (CLEAN-05): la liquidación completa la define `preview.IsFullyPaid` del
                // servidor; ya no hay umbral local sobre la deuda.
                if (IsFullLiquidation)
                {
                    targetSale.RoundingAdjustment = RoundingAdjustment;
                    int realId = await _salesService.CompleteSaleAsync(
                        targetSale.Id, CurrentExchangeRate, rawPayments,
                        RoundingAdjustment, _userSession?.CurrentUser?.Id, IsPendingPickup,
                        _currentIdempotencyKey, _cts.Token);

                    _currentIdempotencyKey = null;
                    WeakReferenceMessenger.Default.Unregister<CartUpdatedMessage>(this);
                    _dialogService?.CloseCurrentModal(realId);
                }
                else
                {
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
                    _dialogService?.CloseCurrentModal(-1);
                }
            }
            else
            {
                _sale.RoundingAdjustment = RoundingAdjustment;
                var paymentsList = Payments.Select(p => new SalePaymentDto(p.Dto.PaymentMethodId, p.Dto.Amount, p.AmountBsS, p.Dto.ReferenceNumber));
                int realId = await _salesService.CompleteSaleAsync(
                    _sale.Id, CurrentExchangeRate, paymentsList,
                    RoundingAdjustment, _userSession?.CurrentUser?.Id, IsPendingPickup,
                    _currentIdempotencyKey, _cts.Token);

                _currentIdempotencyKey = null;
                WeakReferenceMessenger.Default.Unregister<CartUpdatedMessage>(this);
                _dialogService?.CloseCurrentModal(realId);
            }
        }
        catch (System.OperationCanceledException)
        {
            ShowWarning("Cobro Cancelado", "El proceso de cobro fue cancelado.");
        }
        catch (System.Exception ex)
        {
            ShowError("Error al Procesar Cobro", $"Error al procesar el cobro: {ex.Message}. Verifique la conexión con el servidor e intente nuevamente.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsProcessing = false;
        }
    }
}
