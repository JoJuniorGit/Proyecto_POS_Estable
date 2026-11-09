using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.Common;
using Core.Helpers;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>POS-UI-842: filtro rapido del libro diario de caja.</summary>
public enum CashMovementTypeFilter
{
    All,
    Income,
    Expense
}

public partial class CashDrawerViewModel : ObservableObject, IDisposable
{
    private readonly ICashDrawerService _cashDrawerService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly IDialogService? _dialogService;
    private readonly IPaymentService? _paymentService;
    private readonly UserSession? _userSession;
    private readonly IDispatcherInvoker _dispatcherInvoker;

    private CashDrawerSessionDto? _activeSession;
    public CashDrawerSessionDto? ActiveSession
    {
        get => _activeSession;
        set
        {
            if (SetProperty(ref _activeSession, value))
            {
                OnPropertyChanged(nameof(IsSessionActive));
                OnPropertyChanged(nameof(IsSessionInactive));
                OnPropertyChanged(nameof(CanOpenCashDrawer));
                OnPropertyChanged(nameof(CanUsePettyCash));
                OnPropertyChanged(nameof(CanCloseCashDrawer));
                if (value is null)
                {
                    IsPettyCashPanelOpen = false;
                }
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

    private string _formattedBalanceUsd = FormatPrimaryAmount(0m);
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

    private string _formattedTotalIncomeBsS = CurrencyDisplay.FormatSecondary(0m, 0);
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

    private string _formattedTotalExpenseBsS = CurrencyDisplay.FormatSecondary(0m, 0);
    public string FormattedTotalExpenseBsS
    {
        get => _formattedTotalExpenseBsS;
        set => SetProperty(ref _formattedTotalExpenseBsS, value);
    }

    public ObservableCollection<CashTransactionDto> RecentIncomes { get; } = new();
    public ObservableCollection<CashTransactionDto> OrderedTransactions { get; } = new();

    // 8.212-WU4: live per-method physical balances and the editable opening breakdown.
    public ObservableCollection<PhysicalMethodBalanceClientDto> PhysicalMethodBalances { get; } = new();

    // S1/S2: resumen por moneda activa (local primero), calculado por el backend.
    public ObservableCollection<CurrencySessionSummaryViewModel> CurrencySummaries { get; } = new();
    public ObservableCollection<OpeningPhysicalMethodViewModel> OpeningPhysicalMethods { get; } = new();

    public bool HasPhysicalMethodBalances => PhysicalMethodBalances.Count > 0;
    public bool HasOpeningPhysicalMethods => OpeningPhysicalMethods.Count > 0;
    public bool ShowLegacyOpeningInputs => !HasOpeningPhysicalMethods;

    private const int HistoryWindowSize = 300;
    private const int FallbackTransferPaymentMethodId = 2;
    private const int FallbackPosPaymentMethodId = 3;
    private const int FallbackMobilePaymentMethodId = 4;

    // ── Pagination for Physical Cash Transactions (25 per page) ──
    private int _currentPage = 1;
    public int CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    public int PageSize => 25;
    private readonly System.Collections.Generic.List<CashTransactionDto> _allPhysicalTransactions = new();
    private readonly System.Collections.Generic.List<CashTransactionDto> _filteredPhysicalTransactions = new();

    // POS-UI-842: busqueda y filtro rapido aplicados en memoria sobre el historial cargado.
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private CashMovementTypeFilter _movementTypeFilter = CashMovementTypeFilter.All;

    [ObservableProperty]
    private bool _isRecentIncomesExpanded;

    partial void OnSearchTextChanged(string value) => ApplyTransactionFilter();

    partial void OnMovementTypeFilterChanged(CashMovementTypeFilter value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterIncome));
        OnPropertyChanged(nameof(IsFilterExpense));
        ApplyTransactionFilter();
    }

    public bool IsFilterAll => MovementTypeFilter == CashMovementTypeFilter.All;
    public bool IsFilterIncome => MovementTypeFilter == CashMovementTypeFilter.Income;
    public bool IsFilterExpense => MovementTypeFilter == CashMovementTypeFilter.Expense;
    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(SearchText) || MovementTypeFilter != CashMovementTypeFilter.All;

    public int TotalPhysicalTransactions => _filteredPhysicalTransactions.Count;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalPhysicalTransactions / PageSize));

    public bool CanGoPrevious => CurrentPage > 1;
    public bool CanGoNext => CurrentPage < TotalPages;

    public string PaginationSummary => TotalPhysicalTransactions == 0
        ? (HasActiveFilter ? "No hay movimientos para el filtro aplicado" : "No hay movimientos registrados")
        : $"Mostrando {Math.Min((CurrentPage - 1) * PageSize + 1, TotalPhysicalTransactions)} a {Math.Min(CurrentPage * PageSize, TotalPhysicalTransactions)} de {TotalPhysicalTransactions} movimientos";

    public string CurrentPageDisplay => $"Página {CurrentPage} de {TotalPages}";

    public bool IsSessionActive => ActiveSession != null;
    public bool HasRecentIncomes => RecentIncomes.Count > 0;
    public bool IsAdmin => _userSession?.IsAdmin == true;

    /// <summary>
    /// 8.153 (SEC-06): arqueo ciego. Solo Admin/Manager ven el efectivo teórico de la caja; sin
    /// sesión el permiso falla cerrado (mismo criterio que <see cref="IsAdmin"/>).
    /// </summary>
    public bool CanViewTheoreticalBalance => _userSession?.IsAdmin == true || _userSession?.IsManager == true;

    /// <summary>
    /// 8.201 (S1): apertura manual con fondo dual. Sin sesión activa, el overlay de apertura
    /// declara el fondo Bs.S y el USD; el backend los persiste por separado.
    /// </summary>
    public bool IsSessionInactive => !IsSessionActive;
    public bool CanOpenCashDrawer => IsSessionInactive && (_userSession == null || _userSession.IsLoggedIn);

    private string _openingBalanceLocalText = "0";
    public string OpeningBalanceLocalText
    {
        get => _openingBalanceLocalText;
        set => SetProperty(ref _openingBalanceLocalText, value);
    }

    private string _openingBalanceUsdText = "0";
    public string OpeningBalanceUsdText
    {
        get => _openingBalanceUsdText;
        set => SetProperty(ref _openingBalanceUsdText, value);
    }

    private bool _isOpeningSession;
    public bool IsOpeningSession
    {
        get => _isOpeningSession;
        set => SetProperty(ref _isOpeningSession, value);
    }

    public string OpeningRateDisplay =>
        $"Tasa: {CurrencyDisplay.Number(_exchangeRateService.CurrentRate)} / {CurrencyDisplay.PrimaryCurrencyCode}";


    public CashDrawerViewModel(
        ICashDrawerService cashDrawerService, 
        IExchangeRateService exchangeRateService, 
        IDialogService? dialogService = null,
        IPaymentService? paymentService = null,
        UserSession? userSession = null,
        IDispatcherInvoker? dispatcherInvoker = null)
    {
        _cashDrawerService = cashDrawerService;
        _exchangeRateService = exchangeRateService;
        _dialogService = dialogService;
        _paymentService = paymentService;
        _userSession = userSession;
        _dispatcherInvoker = dispatcherInvoker ?? new InlineDispatcherInvoker();

        RecentIncomes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentIncomes));
        PettyCashExpenses.CollectionChanged += (_, _) => NotifyPettyCashChanged();
        PhysicalMethodBalances.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPhysicalMethodBalances));
        OpeningPhysicalMethods.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasOpeningPhysicalMethods));
            OnPropertyChanged(nameof(ShowLegacyOpeningInputs));
        };

        WeakReferenceMessenger.Default.Register<TimeZoneChangedMessage>(this, (r, m) =>
        {
            _dispatcherInvoker.Invoke(() => RefreshAsync().SafeFireAndForget("CashDrawer.TimeZoneChanged"));
        });

        WeakReferenceMessenger.Default.Register<Desktop.Client.Messages.CurrencyRateChangedMessage>(this, (r, m) =>
        {
            var vm = (CashDrawerViewModel)r;
            if (vm.ActiveSession != null)
            {
                vm.UpdateFormattedBalances();
            }

            vm.OnPropertyChanged(nameof(OpeningRateDisplay));
        });

        // 8.143: si el ajuste de moneda cambia en caliente (Settings o SignalR), re-formatear los
        // montos visibles de la caja abierta.
        WeakReferenceMessenger.Default.Register<Desktop.Client.Messages.CurrencyFormatChangedMessage>(this, (r, m) =>
        {
            _dispatcherInvoker.Invoke(() => ((CashDrawerViewModel)r).UpdateFormattedBalances());
        });

        WeakReferenceMessenger.Default.Register<Desktop.Client.Messages.ShiftClosedMessage>(this, (r, m) =>
        {
            _dispatcherInvoker.Invoke(() => RefreshAsync().SafeFireAndForget("CashDrawer.ShiftClosed"));
        });

        // S1/S2: el resumen por moneda sigue al perfil y al formato activos. El mensaje usa
        // WeakReferenceMessenger (se libera con Dispose) para no dejar handlers estáticos vivos.
        WeakReferenceMessenger.Default.Register<Desktop.Client.Messages.MonetaryProfileChangedMessage>(this, (r, m) =>
        {
            _dispatcherInvoker.Invoke(((CashDrawerViewModel)r).UpdateFormattedBalances);
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
        // 8.153 (SEC-06): los no-supervisores no ven el efectivo esperado; "—" evita un "0"
        // engañoso. Ingresos/egresos y el resto de la vista quedan intactos.
        if (CanViewTheoreticalBalance)
        {
            // 8.143: los montos visibles honran el ajuste activo (Venezolano/Internacional) y se
            // re-formatean al vuelo cuando el formato cambia.
            FormattedBalanceBsS = CurrencyDisplay.Number(balanceLocal, 0);
            FormattedBalanceUsd = FormatPrimaryAmount(rate > 0 ? balanceLocal / rate : 0);
        }
        else
        {
            FormattedBalanceBsS = "—";
            FormattedBalanceUsd = string.Empty;
        }

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

        FormattedTotalIncomeBsS = CurrencyDisplay.FormatSecondary(TotalIncomeBsS, 0);
        FormattedTotalExpenseBsS = CurrencyDisplay.FormatSecondary(TotalExpenseBsS, 0);

        // S1/S2: refresca el resumen por moneda con el formato/perfil vigente.
        RebuildCurrencySummaries();
    }

    // The USD-equivalent line keeps the historical trailing-symbol shape ("26,00 $") because the
    // existing cash-drawer display contract asserts it; only the symbol becomes profile-driven.
    private static string FormatPrimaryAmount(decimal value)
        => $"{CurrencyDisplay.Number(value)} {CurrencyDisplay.PrimaryCurrencySymbol}";

    private async Task LoadPhysicalMethodBalancesAsync()
    {
        PhysicalMethodBalances.Clear();

        if (ActiveSession is null)
        {
            await LoadOpeningPhysicalMethodsAsync();
            return;
        }

        try
        {
            var balances = ActiveSession.PhysicalMethodBalances;
            if (balances is null)
            {
                balances = (await _cashDrawerService.GetPhysicalMethodBalancesAsync(ActiveSession.Id)).ToList();
            }

            foreach (var balance in balances
                .OrderBy(balance => GetPhysicalCurrencyPriority(balance.CurrencyCode))
                .ThenBy(balance => balance.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(balance => balance.PaymentMethodId))
            {
                PhysicalMethodBalances.Add(balance);
            }
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[CashDrawer] No se pudieron leer los saldos por metodo fisico: {ex.Message}");
        }
    }

    private async Task LoadOpeningPhysicalMethodsAsync()
    {
        OpeningPhysicalMethods.Clear();
        if (_paymentService is null) return;

        try
        {
            var allowedCurrencies = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            {
                CurrencyDisplay.PrimaryCurrencyCode,
                CurrencyDisplay.LocalCurrencyCode
            };
            if (!string.IsNullOrWhiteSpace(CurrencyDisplay.SecondaryCurrencyCode))
            {
                allowedCurrencies.Add(CurrencyDisplay.SecondaryCurrencyCode);
            }

            var methods = await _paymentService.GetActiveMethodsAsync();
            foreach (var method in methods
                .Where(method => method.IsCash && method.IsActive && !method.IsDeleted)
                .Where(method => allowedCurrencies.Contains(ResolveOpeningCurrencyCode(method)))
                .OrderBy(method => method.DisplayOrder)
                .ThenBy(method => method.Id))
            {
                var currencyCode = ResolveOpeningCurrencyCode(method);
                OpeningPhysicalMethods.Add(new OpeningPhysicalMethodViewModel
                {
                    PaymentMethodId = method.Id,
                    Name = method.Name,
                    CurrencyCode = currencyCode,
                    CurrencySymbol = CurrencyDisplay.SymbolFor(currencyCode)
                });
            }
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[CashDrawer] No se pudieron preparar los metodos fisicos de apertura: {ex.Message}");
        }
    }

    private static string ResolveOpeningCurrencyCode(PaymentMethodDto method)
    {
        var raw = method.Currency?.Trim().ToUpperInvariant();
        if (raw is "USD" or "$" or "US$")
        {
            return "USD";
        }

        if (raw is "BS.S" or "BSS" or "VES" or "BS")
        {
            return CurrencyDisplay.LocalCurrencyCode;
        }

        return raw is { Length: 3 } && raw.All(character => character is >= 'A' and <= 'Z')
            ? raw
            : CurrencyDisplay.LocalCurrencyCode;
    }

    public async Task LoadSessionAsync()
    {
        if (_userSession != null && !_userSession.IsLoggedIn) return;

        try
        {
            ActiveSession = await _cashDrawerService.GetActiveSessionAsync();
            await LoadPhysicalMethodBalancesAsync();

            // Historial persistente: movimientos físicos de TODAS las sesiones (activa y cerradas),
            // para conservar la trazabilidad de las sesiones previas tras el cierre de caja.
            var history = await _cashDrawerService.GetHistoryAsync(HistoryWindowSize);
            _allPhysicalTransactions.Clear();
            if (history != null && history.Count > 0)
            {
                _allPhysicalTransactions.AddRange(history.Take(HistoryWindowSize));
            }

            if (ActiveSession != null)
            {
                // 8.153 (SEC-06): el fetch del saldo teórico existe solo para supervisores.
                CurrentBalanceBsS = CanViewTheoreticalBalance
                    ? await _cashDrawerService.GetCurrentBalanceLocalAsync(ActiveSession.Id)
                    : 0;
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
            ApplyTransactionFilter();
            UpdateFormattedBalances();

            // T4 (Paso 13 / 8.188, S1): el panel de caja chica conserva el turno sincronizado.
            if (IsPettyCashPanelOpen)
            {
                await LoadPettyCashAsync();
            }
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Error", $"Error loading cash register: {ex.Message}");
        }
    }

    private void UpdatePaginatedTransactions()
    {
        OrderedTransactions.Clear();
        var pageItems = _filteredPhysicalTransactions
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
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(PaginationSummary));
        OnPropertyChanged(nameof(CurrentPageDisplay));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// POS-UI-842: recompone el conjunto visible (tipo + busqueda) y vuelve a la primera pagina
    /// para que el paginador nunca quede fuera de rango tras cambiar un filtro.
    /// </summary>
    private void ApplyTransactionFilter()
    {
        RebuildFilteredTransactions();
        CurrentPage = 1;
        UpdatePaginatedTransactions();
    }

    private void RebuildFilteredTransactions()
    {
        IEnumerable<CashTransactionDto> query = _allPhysicalTransactions;
        query = MovementTypeFilter switch
        {
            CashMovementTypeFilter.Income => query.Where(tx => tx.Type == CashTransactionType.Income),
            CashMovementTypeFilter.Expense => query.Where(tx => tx.Type == CashTransactionType.Expense),
            _ => query
        };

        var term = SearchText?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            query = query.Where(tx => MatchesMovementSearch(tx, term));
        }

        _filteredPhysicalTransactions.Clear();
        _filteredPhysicalTransactions.AddRange(query);
    }

    private static bool MatchesMovementSearch(CashTransactionDto transaction, string term)
    {
        if (transaction.InvoiceNumber.HasValue
            && transaction.InvoiceNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                .Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(transaction.Description)
            && transaction.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return transaction.FormattedInvoiceNumber.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void SetMovementTypeFilter(CashMovementTypeFilter filter) => MovementTypeFilter = filter;

    [RelayCommand]
    private void ToggleRecentIncomes() => IsRecentIncomesExpanded = !IsRecentIncomesExpanded;

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

    /// <summary>
    /// 8.201 (S1): abre la sesión con fondo inicial Bs.S + USD. El auto-alta por primer cobro no
    /// se toca: este comando es la apertura explícita desde la vista de caja.
    /// </summary>
    [RelayCommand]
    private async Task OpenCashDrawerAsync()
    {
        if (IsSessionActive || IsOpeningSession) return;

        decimal local = 0m;
        decimal usd = 0m;
        System.Collections.Generic.List<OpenSessionMethodBalanceClientRequest>? methodBalances = null;

        if (HasOpeningPhysicalMethods)
        {
            methodBalances = new System.Collections.Generic.List<OpenSessionMethodBalanceClientRequest>();
            foreach (var entry in OpeningPhysicalMethods)
            {
                if (!FlexibleDecimalParser.TryParse(entry.AmountText, out decimal amount) || amount < 0m)
                {
                    _dialogService?.ShowWarning(
                        "Apertura de Caja",
                        $"Ingrese un fondo inicial válido para {entry.Name} ({entry.CurrencyCode}) (mayor o igual a cero).");
                    return;
                }

                methodBalances.Add(new OpenSessionMethodBalanceClientRequest
                {
                    PaymentMethodId = entry.PaymentMethodId,
                    OpeningAmount = amount
                });

                if (string.Equals(entry.CurrencyCode, CurrencyDisplay.LocalCurrencyCode, StringComparison.Ordinal))
                {
                    local += amount;
                }
                else if (string.Equals(entry.CurrencyCode, "USD", StringComparison.Ordinal))
                {
                    usd += amount;
                }
            }
        }
        else
        {
            if (!FlexibleDecimalParser.TryParse(OpeningBalanceLocalText, out local) || local < 0m)
            {
                _dialogService?.ShowWarning(
                    "Apertura de Caja",
                    $"Ingrese un fondo inicial válido en {CurrencyDisplay.LocalCurrencyCode} (mayor o igual a cero).");
                return;
            }

            if (!FlexibleDecimalParser.TryParse(OpeningBalanceUsdText, out usd) || usd < 0m)
            {
                _dialogService?.ShowWarning(
                    "Apertura de Caja",
                    $"Ingrese un fondo inicial válido en {CurrencyDisplay.PrimaryCurrencyCode} (mayor o igual a cero).");
                return;
            }
        }

        decimal rate = _exchangeRateService.CurrentRate;
        if (rate <= 0m)
        {
            _dialogService?.ShowWarning("Apertura de Caja", "Configure la tasa de cambio vigente antes de abrir la caja.");
            return;
        }

        IsOpeningSession = true;
        try
        {
            if (methodBalances is { Count: > 0 })
            {
                await _cashDrawerService.OpenSessionAsync(local, rate, usd, methodBalances);
            }
            else
            {
                await _cashDrawerService.OpenSessionAsync(local, rate, usd);
            }

            OpeningBalanceLocalText = "0";
            OpeningBalanceUsdText = "0";
            foreach (var entry in OpeningPhysicalMethods)
            {
                entry.AmountText = "0";
            }

            await LoadSessionAsync();
        }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Apertura de Caja", $"No se pudo abrir la caja: {ex.Message}");
        }
        finally
        {
            IsOpeningSession = false;
        }
    }

    [RelayCommand]
    private async Task ProcessCashInAsync()
    {
        if (ActiveSession == null || _dialogService == null) return;

        if (!IsAdmin)
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

        if (!IsAdmin)
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
                    new Desktop.Client.Services.PaymentMethodDto { Id = FallbackTransferPaymentMethodId, Name = "Transferencia", IsCash = false, DisplayOrder = 1 },
                    new Desktop.Client.Services.PaymentMethodDto { Id = FallbackPosPaymentMethodId, Name = "Punto de Venta", IsCash = false, DisplayOrder = 2 },
                    new Desktop.Client.Services.PaymentMethodDto { Id = FallbackMobilePaymentMethodId, Name = "Pago Móvil", IsCash = false, DisplayOrder = 3 }
                };

            // 8.153 (SEC-06): sin permiso de supervisión el diálogo recibe null y opera sin tope
            // local; el servidor sigue validando el efectivo disponible.
            decimal? currentBalance = CanViewTheoreticalBalance
                ? await _cashDrawerService.GetCurrentBalanceLocalAsync(ActiveSession.Id)
                : null;
            CashAdvanceRegisterDialogResult? dialogRes = await _dialogService.ShowCashAdvanceRegisterDialogWithMethodsAsync(
                paymentMethods,
                currentBalance,
                PhysicalMethodBalances.ToList());

            // Compatibility: headless test doubles and older dialog implementations only expose the
            // legacy contract. Fall back to it when the method-aware dialog is unavailable.
            if (dialogRes is null)
            {
                var legacy = await _dialogService.ShowCashAdvanceRegisterDialogAsync(paymentMethods, currentBalance);
                if (legacy is { success: true } legacyResult)
                {
                    dialogRes = new CashAdvanceRegisterDialogResult(
                        true,
                        legacyResult.requestedAmount,
                        legacyResult.commissionAmount,
                        legacyResult.paymentMethodId,
                        legacyResult.paymentMethodName,
                        legacyResult.isTransfer,
                        null,
                        null,
                        legacyResult.paymentMethodId);
                }
            }

            if (dialogRes is { Success: true } res)
            {
                var rate = _exchangeRateService.CurrentRate;

                var advanceResult = res.DispensedCashPaymentMethodId.HasValue
                    ? await _cashDrawerService.ProcessCashAdvanceAsync(
                        ActiveSession.Id,
                        res.RequestedAmount,
                        res.PaymentMethodId,
                        res.PaymentMethodName,
                        res.IsTransfer,
                        rate,
                        res.DispensedCashPaymentMethodId,
                        res.DispensedAmount,
                        res.ReceivedPaymentMethodId ?? res.PaymentMethodId)
                    : await _cashDrawerService.ProcessCashAdvanceAsync(
                        ActiveSession.Id,
                        res.RequestedAmount,
                        res.PaymentMethodId,
                        res.PaymentMethodName,
                        res.IsTransfer,
                        rate);

                string invoiceInfo = advanceResult?.InvoiceNumber.HasValue == true
                    ? $" (Factura N° {advanceResult.InvoiceNumber.Value})"
                    : string.Empty;

                _dialogService.ShowSuccessDialog(
                    $"Adelanto de {CurrencyDisplay.FormatSecondary(res.DispensedAmount ?? res.RequestedAmount)} procesado con éxito{invoiceInfo}. Registrado en el Historial de Ventas.");
                await LoadSessionAsync();
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error de Adelanto", $"No se pudo procesar el adelanto: {ex.Message}");
        }
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private int GetPhysicalCurrencyPriority(string? currencyCode)
    {
        var normalized = NormalizePhysicalCurrencyCode(currencyCode);
        if (string.IsNullOrEmpty(normalized)) return 3;
        if (normalized == NormalizePhysicalCurrencyCode(CurrencyDisplay.LocalCurrencyCode)) return 0;
        if (normalized == NormalizePhysicalCurrencyCode(CurrencyDisplay.PrimaryCurrencyCode)) return 1;
        if (normalized == NormalizePhysicalCurrencyCode(CurrencyDisplay.SecondaryCurrencyCode)) return 2;
        return 3;
    }

    private static string NormalizePhysicalCurrencyCode(string? currencyCode)
    {
        var normalized = currencyCode?.Trim().ToUpperInvariant();
        if (normalized is "BS.S" or "BSS" or "VES") return "VES";
        return normalized ?? string.Empty;
    }

    private void RebuildCurrencySummaries()
    {
        CurrencySummaries.Clear();

        // Sin sesión activa, el perfil sigue definiendo las monedas visibles: se muestran en
        // cero para que la caja tenga una fila local estable y el operador vea el régimen.
        if (ActiveSession is null)
        {
            AddProfileCurrencySummary(CurrencyDisplay.LocalCurrencyCode, isLocal: true);
            AddProfileCurrencySummary(CurrencyDisplay.PrimaryCurrencyCode, isLocal: false);
            AddProfileCurrencySummary(CurrencyDisplay.SecondaryCurrencyCode, isLocal: false);
            return;
        }

        var rows = ActiveSession.CurrencySummaries;
        if (rows is null)
        {
            return;
        }

        foreach (var row in rows)
        {
            CurrencySummaries.Add(new CurrencySessionSummaryViewModel
            {
                CurrencyCode = row.CurrencyCode,
                CurrencySymbol = row.CurrencySymbol,
                OpeningBalance = row.OpeningBalance,
                TotalPhysicalInflows = row.TotalPhysicalInflows,
                TotalPhysicalOutflows = row.TotalPhysicalOutflows,
                ExpectedCashBalance = row.ExpectedCashBalance,
                IsLocal = row.IsLocal
            });
        }
    }

    private void AddProfileCurrencySummary(string? code, bool isLocal)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (normalized is "BS.S" or "BSS")
        {
            normalized = "VES";
        }

        if (CurrencySummaries.Any(row => row.CurrencyCode == normalized))
        {
            return;
        }

        CurrencySummaries.Add(new CurrencySessionSummaryViewModel
        {
            CurrencyCode = normalized,
            CurrencySymbol = CurrencyDisplay.SymbolFor(normalized),
            OpeningBalance = 0m,
            TotalPhysicalInflows = 0m,
            TotalPhysicalOutflows = 0m,
            ExpectedCashBalance = 0m,
            IsLocal = isLocal
        });
    }
}
