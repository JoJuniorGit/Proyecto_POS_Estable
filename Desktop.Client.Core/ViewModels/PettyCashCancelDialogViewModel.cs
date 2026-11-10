using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// T4 (Paso 13 / 8.188, S5): anulación lógica de un gasto de caja chica. Exige motivo y, cuando la
/// jerarquía lo demanda, delega en el flujo de autorización con el contexto congelado del gasto
/// (sesión, moneda, montos duales y categoría) antes de reintentar con el token.
/// </summary>
public partial class PettyCashCancelDialogViewModel : ObservableObject, IDisposable
{
    public const string Title = "Anular Gasto de Caja Chica";

    private readonly ICashDrawerService _cashDrawerService;
    private readonly IDialogService _dialogService;
    private readonly PettyCashExpenseClientDto _expense;
    private PettyCashExpenseClientDto? _authorizedResult;
    private int _isDisposed;

    public PettyCashCancelDialogViewModel(
        ICashDrawerService cashDrawerService,
        IDialogService dialogService,
        PettyCashExpenseClientDto expense)
    {
        _cashDrawerService = cashDrawerService ?? throw new ArgumentNullException(nameof(cashDrawerService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _expense = expense ?? throw new ArgumentNullException(nameof(expense));
    }

    public Action<bool>? RequestClose;

    public PettyCashExpenseClientDto Expense => _expense;

    public string VoucherCode => _expense.VoucherCode;

    public string ExpenseSummary =>
        $"{PettyCashLabels.Category(_expense.Category)} · {_expense.Concept} · " +
        (CurrencyDisplay.IsSingle
            ? CurrencyDisplay.FormatPrimary(_expense.AmountBsS)
            : $"{CurrencyDisplay.FormatSecondary(_expense.AmountBsS)} / {CurrencyDisplay.FormatPrimary(_expense.AmountUsd)}");
    public PettyCashExpenseClientDto? Result { get; private set; }

    [ObservableProperty]
    private string _reason = string.Empty;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public string? ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Reason)) return "Ingrese el motivo de la anulación.";
            if (Reason.Trim().Length > 500) return "El motivo no puede exceder 500 caracteres.";
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
            var result = await _cashDrawerService.CancelPettyCashExpenseAsync(
                _expense.Id,
                Reason.Trim(),
                authorizationToken: null);
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
                    ? "No se pudo anular el gasto de caja chica."
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
        _authorizedResult = await _cashDrawerService.CancelPettyCashExpenseAsync(
            _expense.Id,
            Reason.Trim(),
            token);
    }

    private void Complete(PettyCashExpenseClientDto result)
    {
        Result = result;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    partial void OnReasonChanged(string value)
    {
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsProcessingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        RequestClose = null;
    }
}

