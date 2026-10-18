using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// T4 (F3, S6): estado de cuenta dual del cliente. Presenta el saldo USD, su equivalente Bs.S a
/// la tasa del día, la cartera y el historial de abonos con el FX realizado; no recalcula saldos.
/// </summary>
public partial class CreditStatementDialogViewModel : ObservableObject
{
    private readonly IDialogService _dialogService;

    public CreditStatementDialogViewModel(CreditStatementClientDto statement, IDialogService dialogService)
    {
        Statement = statement ?? throw new System.ArgumentNullException(nameof(statement));
        _dialogService = dialogService ?? throw new System.ArgumentNullException(nameof(dialogService));
        Receivables = new ObservableCollection<CreditStatementReceivableClientDto>(statement.Receivables);
        Payments = new ObservableCollection<CreditStatementPaymentClientDto>(statement.Payments);
    }

    public CreditStatementClientDto Statement { get; }

    public ObservableCollection<CreditStatementReceivableClientDto> Receivables { get; }

    public ObservableCollection<CreditStatementPaymentClientDto> Payments { get; }

    public string CustomerName => string.IsNullOrWhiteSpace(Statement.Profile.CustomerName)
        ? "Cliente"
        : Statement.Profile.CustomerName;

    public string CustomerCedula => Statement.Profile.CustomerId > 0
        ? $"Cliente #{Statement.Profile.CustomerId}"
        : string.Empty;

    public decimal TodayRate => Statement.TodayRate;

    public decimal CurrentDebtUSD => Statement.Profile.CurrentDebtUSD;

    public decimal CurrentDebtBsS => Statement.CurrentDebtTodayBsS;

    public decimal AvailableCreditUSD => Statement.Profile.AvailableCreditUSD;

    public decimal UtilizationPercent => Statement.Profile.UtilizationPercent;

    public string StatusLabel => Statement.Profile.StatusLabel;

    public bool HasOverdue => Receivables.Any(row => row.IsOverdue);

    public decimal TotalPaymentsUSD => Payments.Sum(payment => payment.AmountUSD);

    public decimal TotalFxDifferenceBsS => Payments.Sum(payment => payment.FxDifferenceBsS);

    [RelayCommand]
    private void Close()
    {
        _dialogService.CloseCurrentModal(null);
    }
}
