using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.Common;
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
    private readonly ICashDrawerService _cashDrawerService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly IDialogService? _dialogService;
    private readonly IPaymentService? _paymentService;
    private readonly UserSession? _userSession;

    private CashDrawerSessionDto? _activeSession;
    public CashDrawerSessionDto? ActiveSession
    {
        get => _activeSession;
        set
        {
            if (SetProperty(ref _activeSession, value))
            {
                OnPropertyChanged(nameof(IsSessionActive));
            }
        }
    }

    private decimal _currentBalanceBsS;
    public decimal CurrentBalanceBsS
    {
        get => _currentBalanceBsS;
        set
        {
            if (SetProperty(ref _currentBalanceBsS, value))
            {
                UpdateFormattedBalances();
            }
        }
    }

    private string _formattedBalanceBsS = "0";
    public string FormattedBalanceBsS
    {
        get => _formattedBalanceBsS;
        set => SetProperty(ref _formattedBalanceBsS, value);
    }

    private string _formattedBalanceUsd = "0.00 $";
    public string FormattedBalanceUsd
    {
        get => _formattedBalanceUsd;
        set => SetProperty(ref _formattedBalanceUsd, value);
    }

    private decimal _totalIncomeBsS;
    public decimal TotalIncomeBsS
    {
        get => _totalIncomeBsS;
        set => SetProperty(ref _totalIncomeBsS, value);
    }

    private string _formattedTotalIncomeBsS = "0 Bs.S";
    public string FormattedTotalIncomeBsS
    {
        get => _formattedTotalIncomeBsS;
        set => SetProperty(ref _formattedTotalIncomeBsS, value);
    }

    private decimal _totalExpenseBsS;
    public decimal TotalExpenseBsS
    {
        get => _totalExpenseBsS;
        set => SetProperty(ref _totalExpenseBsS, value);
    }

    private string _formattedTotalExpenseBsS = "0 Bs.S";
    public string FormattedTotalExpenseBsS
    {
        get => _formattedTotalExpenseBsS;
        set => SetProperty(ref _formattedTotalExpenseBsS, value);
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
    public bool IsAdmin => _userSession == null || _userSession.IsAdmin;


    public CashDrawerViewModel(
        ICashDrawerService cashDrawerService, 
        IExchangeRateService exchangeRateService, 
        IDialogService? dialogService = null,
        IPaymentService? paymentService = null,
        UserSession? userSession = null)
    {
        _cashDrawerService = cashDrawerService;
        _exchangeRateService = exchangeRateService;
        _dialogService = dialogService;
        _paymentService = paymentService;
        _userSession = userSession;

        RecentIncomes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentIncomes));

        WeakReferenceMessenger.Default.Register<TimeZoneChangedMessage>(this, (r, m) =>
        {
            Application.Current.Dispatcher.Invoke(() => RefreshAsync().SafeFireAndForget("CashDrawer.TimeZoneChanged"));
        });

        WeakReferenceMessenger.Default.Register<Desktop.Client.Messages.CurrencyRateChangedMessage>(this, (r, m) =>
        {
            var vm = (CashDrawerViewModel)r;
            if (vm.ActiveSession != null)
            {
                vm.UpdateFormattedBalances();
            }
        });

        WeakReferenceMessenger.Default.Register<Desktop.Client.Messages.ShiftClosedMessage>(this, (r, m) =>
        {
            Application.Current.Dispatcher.Invoke(() => RefreshAsync().SafeFireAndForget("CashDrawer.ShiftClosed"));
        });

        if (_userSession == null || _userSession.IsLoggedIn)
        {
            LoadSessionAsync().SafeFireAndForget("CashDrawer.InitialLoad");
        }
    }

    private void UpdateFormattedBalances()
    {
        var rate = _exchangeRateService.CurrentRate;
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
        if (_userSession != null && !_userSession.IsLoggedIn) return;

        try
        {
            ActiveSession = await _cashDrawerService.GetActiveSessionAsync();

            // Historial persistente: movimientos físicos de TODAS las sesiones (activa y cerradas),
            // para conservar la trazabilidad de las sesiones previas tras el cierre de caja.
            var history = await _cashDrawerService.GetHistoryAsync();
            _allPhysicalTransactions.Clear();
            if (history != null && history.Count > 0)
            {
                _allPhysicalTransactions.AddRange(history);
            }

            if (ActiveSession != null)
            {
                CurrentBalanceBsS = await _cashDrawerService.GetCurrentBalanceLocalAsync(ActiveSession.Id);
                RecentIncomes.Clear();
                if (ActiveSession.Transactions != null)
                {
                    // Requirement 1: Display ONLY the last 10 received incomes of the ACTIVE session
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
            }
            else
            {
                CurrentBalanceBsS = 0;
                RecentIncomes.Clear();
            }
            CurrentPage = 1;
            UpdatePaginatedTransactions();
            UpdateFormattedBalances();
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Error", $"Error loading cash register: {ex.Message}");
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
        if (ActiveSession == null || _dialogService == null) return;

        if (_userSession != null && !_userSession.IsAdmin)
        {
            _dialogService.ShowError("Acceso Denegado", "Solo los usuarios Administradores tienen permiso para realizar operaciones de CASH IN.");
            return;
        }

        var dialogRes = await _dialogService.ShowCashTransactionDialogAsync("Cash In (Add Funds)");

        if (dialogRes is { } res && res.success)
        {
            var rate = _exchangeRateService.CurrentRate;
            if (rate <= 0)
            {
                if (_dialogService != null) _dialogService.ShowWarning("Warning", "Exchange rate not set. Cannot process transaction.");
                return;
            }

            try
            {
                // Requirement 2: Format "{Description} - {Usuario Admin}", max 40 chars for description
                string cleanReason = string.IsNullOrWhiteSpace(res.reason) ? "Ingreso de Caja" : res.reason.Trim();
                if (cleanReason.Length > 40) cleanReason = cleanReason.Substring(0, 40).Trim();

                string adminUser = _userSession?.CurrentUser?.Name ?? _userSession?.CurrentUser?.Cedula ?? "Admin";
                string formattedDescription = $"{cleanReason} - {adminUser}";

                await _cashDrawerService.AddTransactionAsync(
                    ActiveSession.Id,
                    res.amount,
                    CashTransactionType.Income,
                    CashTransactionSource.CashIn,
                    formattedDescription,
                    rate);

                await LoadSessionAsync();
            }
            catch (Exception ex)
            {
                if (_dialogService != null) _dialogService.ShowError("Error", $"Failed to add cash: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task ProcessCashOutAsync()
    {
        if (ActiveSession == null || _dialogService == null) return;

        if (_userSession != null && !_userSession.IsAdmin)
        {
            _dialogService.ShowError("Acceso Denegado", "Solo los usuarios Administradores tienen permiso para realizar operaciones de CASH OUT.");
            return;
        }

        var dialogRes = await _dialogService.ShowCashTransactionDialogAsync("Cash Out (Withdraw Funds)");

        if (dialogRes is { } res && res.success)
        {
            var rate = _exchangeRateService.CurrentRate;
            if (rate <= 0)
            {
                if (_dialogService != null) _dialogService.ShowWarning("Warning", "Exchange rate not set. Cannot process transaction.");
                return;
            }

            try
            {
                // Requirement 2: Format "{Description} - {Usuario Admin}", max 40 chars for description
                string cleanReason = string.IsNullOrWhiteSpace(res.reason) ? "Retiro de Caja" : res.reason.Trim();
                if (cleanReason.Length > 40) cleanReason = cleanReason.Substring(0, 40).Trim();

                string adminUser = _userSession?.CurrentUser?.Name ?? _userSession?.CurrentUser?.Cedula ?? "Admin";
                string formattedDescription = $"{cleanReason} - {adminUser}";

                await _cashDrawerService.AddTransactionAsync(
                    ActiveSession.Id,
                    res.amount,
                    CashTransactionType.Expense,
                    CashTransactionSource.CashOut,
                    formattedDescription,
                    rate);

                await LoadSessionAsync();
            }
            catch (Exception ex)
            {
                if (_dialogService != null) _dialogService.ShowError("Error", $"Failed to withdraw cash: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task ProcessCashAdvanceAsync()
    {
        if (ActiveSession == null || _dialogService == null) return;

        try
        {
            var paymentMethods = _paymentService != null
                ? (await _paymentService.GetActiveMethodsAsync()).ToList()
                : new System.Collections.Generic.List<Desktop.Client.Services.PaymentMethodDto>
                {
                    new Desktop.Client.Services.PaymentMethodDto { Id = 2, Name = "Transferencia", IsCash = false, DisplayOrder = 1 },
                    new Desktop.Client.Services.PaymentMethodDto { Id = 3, Name = "Punto de Venta", IsCash = false, DisplayOrder = 2 },
                    new Desktop.Client.Services.PaymentMethodDto { Id = 4, Name = "Pago Móvil", IsCash = false, DisplayOrder = 3 }
                };

            var currentBalance = await _cashDrawerService.GetCurrentBalanceLocalAsync(ActiveSession.Id);
            var dialogRes = await _dialogService.ShowCashAdvanceRegisterDialogAsync(paymentMethods, currentBalance);

            if (dialogRes is { } res && res.success)
            {
                var rate = _exchangeRateService.CurrentRate;
                var cashierId = _userSession?.CurrentUser?.Id;
                var userName = _userSession?.CurrentUser?.Name ?? _userSession?.CurrentUser?.Cedula ?? "Usuario";

                var advanceResult = await _cashDrawerService.ProcessCashAdvanceAsync(
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

                _dialogService.ShowSuccessDialog($"Adelanto de {res.requestedAmount:N0} Bs.S procesado con éxito{invoiceInfo}. Registrado en el Historial de Ventas.");
                await LoadSessionAsync();
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error de Adelanto", $"No se pudo procesar el adelanto: {ex.Message}");
        }
    }
}
