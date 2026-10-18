using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Helpers;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>Opción de moneda física del egreso (etiqueta formal en español).</summary>
public sealed record PettyCashCurrencyOption(PettyCashCurrencyClient Value, string Label);

/// <summary>Opción tipificada de categoría operativa (S1).</summary>
public sealed record PettyCashCategoryOption(PettyCashCategoryClient Value, string Label);

/// <summary>
/// T4 (Paso 13 / 8.188, S1/S4): modal de registro de un egreso de caja chica. El operador elige la
/// divisa física, captura el nominal y ve la equivalencia dual a la tasa vigente; el servidor fija
/// el snapshot definitivo. Ante un 403 de autorización, delega en el flujo de espera genérico y
/// reintenta con el token aprobado.
/// </summary>
public partial class PettyCashExpenseDialogViewModel : ObservableObject, IDisposable
{
    public const string Title = "Registrar Gasto de Caja Chica";

    private readonly ICashDrawerService _cashDrawerService;
    private readonly IDialogService _dialogService;
    private readonly int _sessionId;
    private readonly decimal _exchangeRate;
    private string? _idempotencyKey;
    private PettyCashExpenseClientDto? _authorizedResult;
    private int _isDisposed;

    public PettyCashExpenseDialogViewModel(
        ICashDrawerService cashDrawerService,
        IDialogService dialogService,
        int sessionId,
        decimal exchangeRate)
    {
        _cashDrawerService = cashDrawerService ?? throw new ArgumentNullException(nameof(cashDrawerService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _sessionId = sessionId;
        _exchangeRate = exchangeRate;

        CurrencyOptions = new[]
        {
            new PettyCashCurrencyOption(PettyCashCurrencyClient.BsS, "Bs.S (bolívares)"),
            new PettyCashCurrencyOption(PettyCashCurrencyClient.Usd, "USD (divisas)")
        };
        CategoryOptions = new[]
        {
            new PettyCashCategoryOption(PettyCashCategoryClient.InsumosLimpieza, PettyCashLabels.Category(PettyCashCategoryClient.InsumosLimpieza)),
            new PettyCashCategoryOption(PettyCashCategoryClient.FletesAcarreo, PettyCashLabels.Category(PettyCashCategoryClient.FletesAcarreo)),
            new PettyCashCategoryOption(PettyCashCategoryClient.MantenimientoReparaciones, PettyCashLabels.Category(PettyCashCategoryClient.MantenimientoReparaciones)),
            new PettyCashCategoryOption(PettyCashCategoryClient.ServiciosPublicos, PettyCashLabels.Category(PettyCashCategoryClient.ServiciosPublicos)),
            new PettyCashCategoryOption(PettyCashCategoryClient.Papeleria, PettyCashLabels.Category(PettyCashCategoryClient.Papeleria)),
            new PettyCashCategoryOption(PettyCashCategoryClient.Varios, PettyCashLabels.Category(PettyCashCategoryClient.Varios))
        };

        _selectedCurrency = CurrencyOptions[0];
    }

    /// <summary>La vista cierra la ventana con true (registrado) o false (cancelado).</summary>
    public Action<bool>? RequestClose;

    public IReadOnlyList<PettyCashCurrencyOption> CurrencyOptions { get; }

    public IReadOnlyList<PettyCashCategoryOption> CategoryOptions { get; }

    public int SessionId => _sessionId;

    public decimal ExchangeRate => _exchangeRate;

    public PettyCashExpenseClientDto? Result { get; private set; }

    [ObservableProperty]
    private PettyCashCurrencyOption _selectedCurrency;

    [ObservableProperty]
    private PettyCashCategoryOption? _selectedCategory;

    [ObservableProperty]
    private string _amountText = string.Empty;

    [ObservableProperty]
    private string _concept = string.Empty;

    [ObservableProperty]
    private string _beneficiary = string.Empty;

    [ObservableProperty]
    private string _referenceNumber = string.Empty;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool IsUsd => SelectedCurrency.Value == PettyCashCurrencyClient.Usd;

    public decimal Amount => CheckoutViewModel.ParseAmount(AmountText);

    /// <summary>Proyección informativa a la tasa vigente; el servidor fija el snapshot real.</summary>
    public decimal EquivalentAmount => _exchangeRate <= 0m
        ? 0m
        : IsUsd
            ? PricingCalculator.ToBsS(Amount, _exchangeRate)
            : PricingCalculator.ToUSD(Amount, _exchangeRate, decimals: 4);

    public string EquivalentDisplay => _exchangeRate <= 0m
        ? "Configure la tasa de cambio vigente."
        : IsUsd
            ? $"≈ Bs.S {PettyCashLabels.FormatBsS(EquivalentAmount)}"
            : $"≈ {PettyCashLabels.FormatUsd(EquivalentAmount)}";

    public string AmountHint => IsUsd ? "Monto (USD)" : "Monto (Bs.S)";

    public string? ValidationMessage
    {
        get
        {
            if (_exchangeRate <= 0m) return "No hay una tasa de cambio vigente para valorar el gasto.";
            if (Amount <= 0m) return "Ingrese un monto mayor a cero.";
            if (SelectedCategory is null) return "Seleccione la categoría operativa del gasto.";
            if (string.IsNullOrWhiteSpace(Concept)) return "Ingrese el concepto del gasto.";
            if (Concept.Trim().Length > 500) return "El concepto no puede exceder 500 caracteres.";
            if (string.IsNullOrWhiteSpace(Beneficiary)) return "Ingrese el beneficiario o proveedor.";
            if (Beneficiary.Trim().Length > 150) return "El beneficiario no puede exceder 150 caracteres.";
            if (string.IsNullOrWhiteSpace(ReferenceNumber)) return "Ingrese la referencia del comprobante.";
            if (ReferenceNumber.Trim().Length > 100) return "La referencia no puede exceder 100 caracteres.";
            return null;
        }
    }

    public bool CanConfirm => !IsProcessing && ValidationMessage is null;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || IsProcessing) return;

        var validation = ValidationMessage;
        if (validation is not null)
        {
            ErrorMessage = validation;
            return;
        }

        IsProcessing = true;
        ErrorMessage = string.Empty;
        try
        {
            _idempotencyKey ??= Guid.NewGuid().ToString("N");
            var result = await _cashDrawerService.RegisterPettyCashExpenseAsync(BuildRequest(), authorizationToken: null);
            if (Volatile.Read(ref _isDisposed) != 0) return;

            Complete(result);
        }
        catch (PettyCashAuthorizationException ex)
        {
            await HandleAuthorizationAsync(ex);
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _isDisposed) == 0)
            {
                ErrorMessage = string.IsNullOrWhiteSpace(ex.Message)
                    ? "No se pudo registrar el gasto de caja chica."
                    : ex.Message;
            }
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task HandleAuthorizationAsync(PettyCashAuthorizationException exception)
    {
        var wait = await _dialogService.ShowAuthorizationWaitAsync(exception.Context, RetryWithTokenAsync);
        if (Volatile.Read(ref _isDisposed) != 0) return;

        if (wait?.Outcome == AuthorizationWaitOutcome.Granted && _authorizedResult is not null)
        {
            Complete(_authorizedResult);
            return;
        }

        ErrorMessage = wait?.Outcome switch
        {
            AuthorizationWaitOutcome.Rejected => string.IsNullOrWhiteSpace(wait?.RejectionReason)
                ? "La autorización fue rechazada."
                : $"La autorización fue rechazada: {wait!.RejectionReason}",
            AuthorizationWaitOutcome.Expired => "La solicitud de autorización expiró. Intente nuevamente.",
            AuthorizationWaitOutcome.Cancelled => "La autorización fue cancelada.",
            _ => "No se pudo completar la operación autorizada."
        };
    }

    private async Task RetryWithTokenAsync(string token)
    {
        _authorizedResult = await _cashDrawerService.RegisterPettyCashExpenseAsync(BuildRequest(), token);
    }

    private PettyCashExpenseClientRequest BuildRequest() => new()
    {
        Currency = SelectedCurrency.Value,
        Amount = Amount,
        Category = SelectedCategory!.Value,
        Concept = Concept.Trim(),
        Beneficiary = Beneficiary.Trim(),
        ReferenceNumber = ReferenceNumber.Trim(),
        IdempotencyKey = _idempotencyKey
    };

    private void Complete(PettyCashExpenseClientDto result)
    {
        Result = result;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    partial void OnSelectedCurrencyChanged(PettyCashCurrencyOption value)
    {
        if (value is null) return;
        NotifyCalculationsChanged();
    }

    partial void OnSelectedCategoryChanged(PettyCashCategoryOption? value) => NotifyValidationChanged();

    partial void OnAmountTextChanged(string value) => NotifyCalculationsChanged();

    partial void OnConceptChanged(string value) => NotifyValidationChanged();

    partial void OnBeneficiaryChanged(string value) => NotifyValidationChanged();

    partial void OnReferenceNumberChanged(string value) => NotifyValidationChanged();

    partial void OnIsProcessingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCalculationsChanged()
    {
        OnPropertyChanged(nameof(IsUsd));
        OnPropertyChanged(nameof(Amount));
        OnPropertyChanged(nameof(EquivalentAmount));
        OnPropertyChanged(nameof(EquivalentDisplay));
        OnPropertyChanged(nameof(AmountHint));
        NotifyValidationChanged();
    }

    private void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        RequestClose = null;
    }
}
