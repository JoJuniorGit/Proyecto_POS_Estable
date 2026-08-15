using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MaterialDesignThemes.Wpf;

namespace Desktop.Client.ViewModels;

public partial class CashDrawerViewModel : ObservableObject
{
    private readonly ICashDrawerService _cash_drawer_service;
    private readonly IExchangeRateService _exchange_rate_service;
    private readonly IDialogService? _dialog_service;
    private readonly IPaymentService? _payment_service;
    private readonly UserSession? _user_session;

    private CashDrawerSessionDto? _active_session;
    public CashDrawerSessionDto? ActiveSession
    {
        get => _active_session;
        set
        {
            if (SetProperty(ref _active_session, value))
            {
                OnPropertyChanged(nameof(IsSessionActive));
            }
        }
    }

    private decimal _current_balance_bs_s;
    public decimal CurrentBalanceBsS
    {
        get => _current_balance_bs_s;
        set
        {
            if (SetProperty(ref _current_balance_bs_s, value))
            {
                UpdateFormattedBalances();
            }
        }
    }

    private string _formatted_balance_bs_s = "0";
    public string FormattedBalanceBsS
    {
        get => _formatted_balance_bs_s;
        set => SetProperty(ref _formatted_balance_bs_s, value);
    }

    private string _formatted_balance_usd = "0.00 $";
    public string FormattedBalanceUsd
    {
        get => _formatted_balance_usd;
        set => SetProperty(ref _formatted_balance_usd, value);
    }

    private decimal _total_income_bs_s;
    public decimal TotalIncomeBsS
    {
        get => _total_income_bs_s;
        set => SetProperty(ref _total_income_bs_s, value);
    }

    private string _formatted_total_income_bs_s = "0 Bs.S";
    public string FormattedTotalIncomeBsS
    {
        get => _formatted_total_income_bs_s;
        set => SetProperty(ref _formatted_total_income_bs_s, value);
    }

    private decimal _total_expense_bs_s;
    public decimal TotalExpenseBsS
    {
        get => _total_expense_bs_s;
        set => SetProperty(ref _total_expense_bs_s, value);
    }

    private string _formatted_total_expense_bs_s = "0 Bs.S";
    public string FormattedTotalExpenseBsS
    {
        get => _formatted_total_expense_bs_s;
        set => SetProperty(ref _formatted_total_expense_bs_s, value);
    }

    public ObservableCollection<CashTransactionDto> RecentIncomes { get; } = new();
    public ObservableCollection<CashTransactionDto> OrderedTransactions { get; } = new();

    // ── Pagination for Physical Cash Transactions (25 per page) ──
    private int _currentPage = 1;
    public int CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    public int PageSize => 25;
    private readonly System.Collections.Generic.List<CashTransactionDto> _allPhysicalTransactions = new();

    public int TotalPhysicalTransactions => _allPhysicalTransactions.Count;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalPhysicalTransactions / PageSize));

    public bool CanGoPrevious => CurrentPage > 1;
    public bool CanGoNext => CurrentPage < TotalPages;

    public string PaginationSummary => TotalPhysicalTransactions == 0 
        ? "No hay movimientos registrados" 
        : $"Mostrando {Math.Min((CurrentPage - 1) * PageSize + 1, TotalPhysicalTransactions)} a {Math.Min(CurrentPage * PageSize, TotalPhysicalTransactions)} de {TotalPhysicalTransactions} movimientos";

    public string CurrentPageDisplay => $"Página {CurrentPage} de {TotalPages}";

    public bool IsSessionActive => ActiveSession != null;
    public bool HasRecentIncomes => RecentIncomes.Count > 0;
    public bool IsAdmin => _user_session == null || _user_session.IsAdmin;


    public CashDrawerViewModel(
        ICashDrawerService cash_drawer_service, 
        IExchangeRateService exchange_rate_service, 
        IDialogService? dialog_service = null,
        IPaymentService? payment_service = null,
        UserSession? user_session = null)
    {
        _cash_drawer_service = cash_drawer_service;
        _exchange_rate_service = exchange_rate_service;
        _dialog_service = dialog_service;
        _payment_service = payment_service;
        _user_session = user_session;

        RecentIncomes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentIncomes));

        WeakReferenceMessenger.Default.Register<TimeZoneChangedMessage>(this, (_r, _m) =>
        {
            Application.Current.Dispatcher.Invoke(() => _ = RefreshAsync());
        });

        WeakReferenceMessenger.Default.Register<Desktop.Client.Messages.CurrencyRateChangedMessage>(this, (_r, _m) =>
        {
            var _vm = (CashDrawerViewModel)_r;
            if (_vm.ActiveSession != null)
            {
                _vm.UpdateFormattedBalances();
            }
        });

        WeakReferenceMessenger.Default.Register<Desktop.Client.Messages.ShiftClosedMessage>(this, (_r, _m) =>
        {
            Application.Current.Dispatcher.Invoke(() => _ = RefreshAsync());
        });

        _ = LoadSessionAsync();
    }

    private void UpdateFormattedBalances()
    {
        var rate = _exchange_rate_service.CurrentRate;
        var balanceLocal = CurrentBalanceBsS;
        FormattedBalanceBsS = balanceLocal.ToString("N0");
        FormattedBalanceUsd = (rate > 0 ? balanceLocal / rate : 0).ToString("N2") + " $";

        if (ActiveSession != null && ActiveSession.Transactions != null)
        {
            TotalIncomeBsS = ActiveSession.Transactions
                .Where(t => t.Type == CashTransactionType.Income && t.Source != CashTransactionSource.Opening && t.IsPhysicalCash)
                .Sum(t => t.AmountLocal);
            TotalExpenseBsS = ActiveSession.Transactions
                .Where(t => t.Type == CashTransactionType.Expense && t.Source != CashTransactionSource.Closing && t.IsPhysicalCash)
                .Sum(t => t.AmountLocal);
        }
        else
        {
            TotalIncomeBsS = 0;
            TotalExpenseBsS = 0;
        }

        FormattedTotalIncomeBsS = TotalIncomeBsS.ToString("N0") + " Bs.S";
        FormattedTotalExpenseBsS = TotalExpenseBsS.ToString("N0") + " Bs.S";
    }

    public async Task LoadSessionAsync()
    {
        try
        {
            ActiveSession = await _cash_drawer_service.GetActiveSessionAsync();
            if (ActiveSession != null)
            {
                CurrentBalanceBsS = await _cash_drawer_service.GetCurrentBalanceLocalAsync(ActiveSession.Id);
                RecentIncomes.Clear();
                _allPhysicalTransactions.Clear();
                if (ActiveSession.Transactions != null)
                {
                    var sortedPhysical = ActiveSession.Transactions
                        .Where(t => t.IsPhysicalCash)
                        .OrderByDescending(t => t.TransactionTimeLocal)
                        .ToList();
                    _allPhysicalTransactions.AddRange(sortedPhysical);

                    // Requirement 1: Display ONLY the last 10 received incomes
                    var recentIncomesList = ActiveSession.Transactions
                        .Where(t => t.Type == CashTransactionType.Income && t.Source != CashTransactionSource.Opening && t.IsPhysicalCash)
                        .OrderByDescending(t => t.TransactionTimeLocal)
                        .Take(10)
                        .ToList();

                    foreach (var inc in recentIncomesList)
                    {
                        RecentIncomes.Add(inc);
                    }
                }
                CurrentPage = 1;
                UpdatePaginatedTransactions();
            }
            else
            {
                CurrentBalanceBsS = 0;
                RecentIncomes.Clear();
                _allPhysicalTransactions.Clear();
                CurrentPage = 1;
                UpdatePaginatedTransactions();
            }
            UpdateFormattedBalances();
        }
        catch (Exception _ex)
        {
            Application.Current.Dispatcher.Invoke(() => MessageBox.Show($"Error loading cash register: {_ex.Message}"));
        }
    }

    private void UpdatePaginatedTransactions()
    {
        OrderedTransactions.Clear();
        var pageItems = _allPhysicalTransactions
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize);
        foreach (var tx in pageItems)
        {
            OrderedTransactions.Add(tx);
        }

        OnPropertyChanged(nameof(TotalPhysicalTransactions));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PaginationSummary));
        OnPropertyChanged(nameof(CurrentPageDisplay));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPage()
    {
        if (CanGoPrevious)
        {
            CurrentPage--;
            UpdatePaginatedTransactions();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage()
    {
        if (CanGoNext)
        {
            CurrentPage++;
            UpdatePaginatedTransactions();
        }
    }


    [RelayCommand]
    private async Task RefreshAsync() => await LoadSessionAsync();

    [RelayCommand]
    private async Task ProcessCashInAsync()
    {
        if (ActiveSession == null || _dialog_service == null) return;

        if (_user_session != null && !_user_session.IsAdmin)
        {
            _dialog_service.ShowError("Acceso Denegado", "Solo los usuarios Administradores tienen permiso para realizar operaciones de CASH IN.");
            return;
        }

        var _dialogRes = await _dialog_service.ShowCashTransactionDialogAsync("Cash In (Add Funds)");

        if (_dialogRes is { } _res && _res.success)
        {
            var _rate = _exchange_rate_service.CurrentRate;
            if (_rate <= 0)
            {
                MessageBox.Show("Exchange rate not set. Cannot process transaction.", "Warning");
                return;
            }

            try
            {
                // Requirement 2: Format "{Description} - {Usuario Admin}", max 40 chars for description
                string cleanReason = string.IsNullOrWhiteSpace(_res.reason) ? "Ingreso de Caja" : _res.reason.Trim();
                if (cleanReason.Length > 40) cleanReason = cleanReason.Substring(0, 40).Trim();

                string adminUser = _user_session?.CurrentUser?.Name ?? _user_session?.CurrentUser?.Cedula ?? "Admin";
                string formattedDescription = $"{cleanReason} - {adminUser}";

                await _cash_drawer_service.AddTransactionAsync(
                    ActiveSession.Id,
                    _res.amount,
                    CashTransactionType.Income,
                    CashTransactionSource.CashIn,
                    formattedDescription,
                    _rate);

                await LoadSessionAsync();
            }
            catch (Exception _ex)
            {
                MessageBox.Show($"Failed to add cash: {_ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task ProcessCashOutAsync()
    {
        if (ActiveSession == null || _dialog_service == null) return;

        if (_user_session != null && !_user_session.IsAdmin)
        {
            _dialog_service.ShowError("Acceso Denegado", "Solo los usuarios Administradores tienen permiso para realizar operaciones de CASH OUT.");
            return;
        }

        var _dialogRes = await _dialog_service.ShowCashTransactionDialogAsync("Cash Out (Withdraw Funds)");

        if (_dialogRes is { } _res && _res.success)
        {
            var _rate = _exchange_rate_service.CurrentRate;
            if (_rate <= 0)
            {
                MessageBox.Show("Exchange rate not set. Cannot process transaction.", "Warning");
                return;
            }

            try
            {
                // Requirement 2: Format "{Description} - {Usuario Admin}", max 40 chars for description
                string cleanReason = string.IsNullOrWhiteSpace(_res.reason) ? "Retiro de Caja" : _res.reason.Trim();
                if (cleanReason.Length > 40) cleanReason = cleanReason.Substring(0, 40).Trim();

                string adminUser = _user_session?.CurrentUser?.Name ?? _user_session?.CurrentUser?.Cedula ?? "Admin";
                string formattedDescription = $"{cleanReason} - {adminUser}";

                await _cash_drawer_service.AddTransactionAsync(
                    ActiveSession.Id,
                    _res.amount,
                    CashTransactionType.Expense,
                    CashTransactionSource.CashOut,
                    formattedDescription,
                    _rate);

                await LoadSessionAsync();
            }
            catch (Exception _ex)
            {
                MessageBox.Show($"Failed to withdraw cash: {_ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task ProcessCashAdvanceAsync()
    {
        if (ActiveSession == null || _dialog_service == null) return;

        try
        {
            var paymentMethods = _payment_service != null
                ? (await _payment_service.GetActiveMethodsAsync()).ToList()
                : new System.Collections.Generic.List<Desktop.Client.Services.PaymentMethodDto>
                {
                    new Desktop.Client.Services.PaymentMethodDto { Id = 2, Name = "Transferencia", IsCash = false, DisplayOrder = 1 },
                    new Desktop.Client.Services.PaymentMethodDto { Id = 3, Name = "Punto de Venta", IsCash = false, DisplayOrder = 2 },
                    new Desktop.Client.Services.PaymentMethodDto { Id = 4, Name = "Pago Móvil", IsCash = false, DisplayOrder = 3 }
                };

            var currentBalance = await _cash_drawer_service.GetCurrentBalanceLocalAsync(ActiveSession.Id);
            var dialogRes = await _dialog_service.ShowCashAdvanceRegisterDialogAsync(paymentMethods, currentBalance);

            if (dialogRes is { } res && res.success)
            {
                var rate = _exchange_rate_service.CurrentRate;
                var cashierId = _user_session?.CurrentUser?.Id;
                var userName = _user_session?.CurrentUser?.Name ?? _user_session?.CurrentUser?.Cedula ?? "Usuario";

                var advanceResult = await _cash_drawer_service.ProcessCashAdvanceAsync(
                    ActiveSession.Id,
                    res.requestedAmount,
                    res.paymentMethodId,
                    res.paymentMethodName,
                    res.isTransfer,
                    rate,
                    cashierId,
                    userName);

                string invoiceInfo = advanceResult?.InvoiceNumber.HasValue == true
                    ? $" (Factura N° {advanceResult.InvoiceNumber.Value})"
                    : string.Empty;

                _dialog_service.ShowSuccessDialog($"Adelanto de {res.requestedAmount:N0} Bs.S procesado con éxito{invoiceInfo}. Registrado en el Historial de Ventas.");
                await LoadSessionAsync();
            }
        }
        catch (Exception ex)
        {
            _dialog_service.ShowError("Error de Adelanto", $"No se pudo procesar el adelanto: {ex.Message}");
        }
    }
}
