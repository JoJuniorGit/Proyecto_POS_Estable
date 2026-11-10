using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Logging;
using Desktop.Client.Navigation;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Conteo físico ciego del cliente WPF. El listado y el detalle no exponen teórico ni costo; el
/// descuadre se carga únicamente para Admin/Manager. La matriz de acceso se comparte con el
/// sidebar mediante <see cref="WpfNavigationPolicy"/>.
/// </summary>
public partial class StockCountViewModel : ObservableObject, IDisposable
{
    public const string AccessDeniedMessage = "Su rol no tiene acceso al conteo físico.";

    private readonly IStockCountClientService _stockCountService;
    private readonly ICategoryService? _categoryService;
    private readonly IDialogService? _dialogService;
    private readonly IDispatcherInvoker _dispatcherInvoker;
    private CancellationTokenSource? _loadCts;
    private bool _hasLoaded;
    private int _disposed;

    public UserSession? UserSession { get; }

    public bool HasAccess => UserSession == null
        || WpfNavigationPolicy.CanAccess(UserSession.CurrentUser?.Role, WpfNavigationPolicy.StockCount);

    public bool CanManage => UserSession == null || UserSession.IsAdmin || UserSession.IsManager;

    public bool CanCount => HasAccess;

    public bool CanCreateSession => CanManage && !IsBusy;

    public bool HasSelectedSession => SelectedSession is not null;

    public bool SelectedSessionIsOpen => SelectedSession?.Status == StockCountStatus.Open;

    public bool SelectedSessionIsClosed => SelectedSession?.Status == StockCountStatus.Closed;

    public bool SelectedSessionIsTerminal => SelectedSession?.Status is StockCountStatus.Applied or StockCountStatus.Cancelled;

    public bool CanScan => CanCount && SelectedSessionIsOpen && !IsBusy && !IsActionPending;

    public bool CanEditItems => CanScan;

    public bool CanReview => CanManage && SelectedSession is not null && !SelectedSessionIsTerminal && !IsReviewLoading;

    public bool ShowReview => CanManage && Review is not null;

    public bool CanClose => CanManage && SelectedSessionIsOpen && !IsActionPending;

    public bool CanApply => CanManage && SelectedSessionIsClosed && !IsActionPending;

    public bool CanCancel => CanManage && (SelectedSessionIsOpen || SelectedSessionIsClosed) && !IsActionPending;

    public bool HasItems => Items.Count > 0;

    public bool HasSessions => Sessions.Count > 0;

    public bool HasNoSessions => !HasSessions;

    public bool HasNoItems => !HasItems;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasScanFeedback => !string.IsNullOrWhiteSpace(ScanFeedback);

    public bool HasCreateError => !string.IsNullOrWhiteSpace(CreateError);

    public bool HasActionFeedback => !string.IsNullOrWhiteSpace(ActionFeedback);

    public string ScanFeedbackColor => ScanFeedbackIsError ? "#B91C1C" : "#047857";

    public ObservableCollection<StockCountSessionRowViewModel> Sessions { get; } = new();

    public ObservableCollection<StockCountItemRowViewModel> Items { get; } = new();

    public ObservableCollection<StockCountReviewItemRowViewModel> ReviewItems { get; } = new();

    public ObservableCollection<StockCountCategoryOption> Categories { get; } = new();

    public IReadOnlyList<StockCountStatusOption> StatusOptions { get; }

    [ObservableProperty] private StockCountStatusOption _selectedStatusOption;
    [ObservableProperty] private StockCountSessionRowViewModel? _selectedSession;
    [ObservableProperty] private StockCountReviewDto? _review;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoadingDetail;
    [ObservableProperty] private bool _isReviewLoading;
    [ObservableProperty] private bool _isActionPending;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _scanCode = string.Empty;
    [ObservableProperty] private string? _scanFeedback;
    [ObservableProperty] private bool _scanFeedbackIsError;
    [ObservableProperty] private bool _isCreatePanelOpen;
    [ObservableProperty] private string _createName = string.Empty;
    [ObservableProperty] private StockCountCategoryOption? _createScopeCategory;
    [ObservableProperty] private string _createNotes = string.Empty;
    [ObservableProperty] private string? _createError;
    [ObservableProperty] private string? _actionFeedback;

    public StockCountViewModel(
        IStockCountClientService stockCountService,
        ICategoryService? categoryService = null,
        IDialogService? dialogService = null,
        UserSession? userSession = null,
        IDispatcherInvoker? dispatcherInvoker = null)
    {
        ArgumentNullException.ThrowIfNull(stockCountService);

        _stockCountService = stockCountService;
        _categoryService = categoryService;
        _dialogService = dialogService;
        _dispatcherInvoker = dispatcherInvoker ?? new InlineDispatcherInvoker();
        UserSession = userSession;

        StatusOptions = CreateStatusOptions();
        _selectedStatusOption = StatusOptions[0];
        Categories.Add(new StockCountCategoryOption(null, "Todo el inventario"));
        _createScopeCategory = Categories[0];

        if (UserSession != null)
        {
            UserSession.SessionChanged += OnSessionChanged;
        }

        Sessions.CollectionChanged += (_, _) => NotifyStateChanged();
        Items.CollectionChanged += (_, _) => NotifyStateChanged();
    }

    partial void OnSelectedSessionChanged(StockCountSessionRowViewModel? value)
    {
        Items.Clear();
        ReviewItems.Clear();
        Review = null;
        ScanFeedback = null;
        ScanFeedbackIsError = false;
        ActionFeedback = null;
        NotifyStateChanged();
    }

    partial void OnSelectedStatusOptionChanged(StockCountStatusOption value)
    {
        if (_hasLoaded)
        {
            LoadSessionsAsync().SafeFireAndForget("StockCountViewModel.StatusFilter");
        }
    }

    partial void OnReviewChanged(StockCountReviewDto? value) => NotifyStateChanged();

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnScanFeedbackChanged(string? value) => OnPropertyChanged(nameof(HasScanFeedback));

    partial void OnCreateErrorChanged(string? value) => OnPropertyChanged(nameof(HasCreateError));

    partial void OnActionFeedbackChanged(string? value) => OnPropertyChanged(nameof(HasActionFeedback));

    partial void OnScanFeedbackIsErrorChanged(bool value) => OnPropertyChanged(nameof(ScanFeedbackColor));

    partial void OnIsBusyChanged(bool value) => NotifyStateChanged();

    partial void OnIsReviewLoadingChanged(bool value) => NotifyStateChanged();

    partial void OnIsActionPendingChanged(bool value) => NotifyStateChanged();

    public async Task EnsureLoadedAsync()
    {
        if (!HasAccess)
        {
            ErrorMessage = AccessDeniedMessage;
            return;
        }

        if (_hasLoaded)
        {
            return;
        }

        _hasLoaded = true;
        await LoadCategoriesAsync();
        await LoadSessionsAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadSessionsAsync();
        if (SelectedSession is not null)
        {
            await LoadDetailAsync(SelectedSession.Id);
        }
    }

    [RelayCommand]
    private async Task RefreshDetailAsync()
    {
        if (SelectedSession is not null)
        {
            await LoadDetailAsync(SelectedSession.Id);
        }
    }

    [RelayCommand]
    private async Task SelectSessionAsync(StockCountSessionRowViewModel? row)
    {
        if (!HasAccess || row is null)
        {
            return;
        }

        SelectedSession = row;
        await LoadDetailAsync(row.Id);
    }

    [RelayCommand]
    private void BackToSessions()
    {
        SelectedSession = null;
        Items.Clear();
        ReviewItems.Clear();
        Review = null;
        ScanFeedback = null;
        ScanFeedbackIsError = false;
        ActionFeedback = null;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void OpenCreatePanel()
    {
        if (!CanManage)
        {
            return;
        }

        IsCreatePanelOpen = true;
        CreateName = string.Empty;
        CreateNotes = string.Empty;
        CreateScopeCategory = Categories.FirstOrDefault();
        CreateError = null;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        IsCreatePanelOpen = false;
        CreateError = null;
    }

    [RelayCommand]
    private async Task SubmitCreateAsync()
    {
        if (!CanManage)
        {
            return;
        }

        var name = (CreateName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            CreateError = "El nombre de la sesión es obligatorio.";
            return;
        }

        if (name.Length > 80)
        {
            CreateError = "El nombre no puede exceder 80 caracteres.";
            return;
        }

        var notes = (CreateNotes ?? string.Empty).Trim();
        if (notes.Length > 300)
        {
            CreateError = "Las notas no pueden exceder 300 caracteres.";
            return;
        }

        IsBusy = true;
        CreateError = null;
        try
        {
            var created = await _stockCountService.CreateAsync(
                new CreateStockCountRequestDto(
                    name,
                    CreateScopeCategory?.Id,
                    string.IsNullOrWhiteSpace(notes) ? null : notes));
            IsCreatePanelOpen = false;
            await LoadSessionsAsync();
            var row = Sessions.FirstOrDefault(session => session.Id == created.Id);
            if (row is not null)
            {
                SelectedSession = row;
                await LoadDetailAsync(created.Id);
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al crear la sesión de conteo: {ex.Message}", nameof(StockCountViewModel));
            CreateError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!CanScan || SelectedSession is null)
        {
            return;
        }

        var code = (ScanCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return;
        }

        IsBusy = true;
        ScanFeedback = null;
        ScanFeedbackIsError = false;
        try
        {
            var item = await _stockCountService.ScanAsync(SelectedSession.Id, code);
            ScanCode = string.Empty;
            ScanFeedback = $"Contado: {item.ProductName} ({item.SKU}) = {FormatQuantity(item.CountedQuantity)}";
            await LoadDetailAsync(SelectedSession.Id);
        }
        catch (Exception ex)
        {
            ScanFeedback = ex.Message;
            ScanFeedbackIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task IncreaseItemAsync(StockCountItemRowViewModel? row)
    {
        if (!CanEditItems || row is null)
        {
            return;
        }

        await SetItemQuantityAsync(row, row.CountedQuantity + 1m);
    }

    [RelayCommand]
    private async Task DecreaseItemAsync(StockCountItemRowViewModel? row)
    {
        if (!CanEditItems || row is null)
        {
            return;
        }

        await SetItemQuantityAsync(row, Math.Max(0m, row.CountedQuantity - 1m));
    }

    [RelayCommand]
    private async Task EditItemAsync(StockCountItemRowViewModel? row)
    {
        if (!CanEditItems || row is null || _dialogService is null)
        {
            return;
        }

        var input = await _dialogService.ShowTextInputAsync(
            $"Cantidad contada para {row.ProductName}",
            "0");
        if (input is null)
        {
            return;
        }

        if (!FlexibleDecimalParser.TryParse(input, out var quantity) || quantity < 0m)
        {
            _dialogService.ShowWarning("Cantidad inválida", "Ingrese una cantidad mayor o igual a cero.");
            return;
        }

        await SetItemQuantityAsync(row, quantity);
    }

    [RelayCommand]
    private async Task RemoveItemAsync(StockCountItemRowViewModel? row)
    {
        if (!CanEditItems || row is null || SelectedSession is null)
        {
            return;
        }

        if (_dialogService is not null
            && !_dialogService.ShowConfirm(
                "Quitar ítem",
                $"¿Quitar '{row.ProductName}' de la sesión de conteo?"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _stockCountService.RemoveItemAsync(SelectedSession.Id, row.ProductId);
            ScanFeedback = $"Ítem quitado: {row.ProductName}";
            ScanFeedbackIsError = false;
            await LoadDetailAsync(SelectedSession.Id);
        }
        catch (Exception ex)
        {
            ScanFeedback = ex.Message;
            ScanFeedbackIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadReviewAsync()
    {
        if (!CanReview || SelectedSession is null)
        {
            return;
        }

        await LoadReviewAsync(SelectedSession.Id);
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (!CanClose || SelectedSession is null)
        {
            return;
        }

        if (_dialogService is not null
            && !_dialogService.ShowConfirm(
                "Cerrar conteo",
                "Al cerrar se congela el teórico y el costo de cada ítem para el descuadre. " +
                "Ya no se podrán escanear ni editar cantidades en esta sesión."))
        {
            return;
        }

        await ExecuteSessionActionAsync(
            "Sesión cerrada. Revise el descuadre y aplique los ajustes.",
            id => _stockCountService.CloseAsync(id));
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply || SelectedSession is null)
        {
            return;
        }

        if (_dialogService is not null
            && !_dialogService.ShowConfirm(
                "Aplicar ajustes",
                "El stock quedará exactamente en las cantidades contadas y se registrará un " +
                "movimiento de Kardex por cada diferencia. Esta acción es irreversible."))
        {
            return;
        }

        await ExecuteSessionActionAsync(
            "Ajustes aplicados. La sesión quedó cerrada definitivamente.",
            id => _stockCountService.ApplyAsync(id));
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (!CanCancel || SelectedSession is null)
        {
            return;
        }

        if (_dialogService is not null
            && !_dialogService.ShowConfirm(
                "Cancelar sesión",
                "La sesión se descartará y no aplicará ajustes al stock. ¿Desea continuar?"))
        {
            return;
        }

        await ExecuteSessionActionAsync(
            "Sesión cancelada. No se aplicaron ajustes al stock.",
            id => _stockCountService.CancelAsync(id));
    }

    private async Task SetItemQuantityAsync(StockCountItemRowViewModel row, decimal quantity)
    {
        if (SelectedSession is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var updated = await _stockCountService.SetCountedQuantityAsync(
                SelectedSession.Id,
                row.ProductId,
                quantity);
            ScanFeedback = $"{updated.ProductName}: {FormatQuantity(updated.CountedQuantity)}";
            ScanFeedbackIsError = false;
            await LoadDetailAsync(SelectedSession.Id);
        }
        catch (Exception ex)
        {
            ScanFeedback = ex.Message;
            ScanFeedbackIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteSessionActionAsync(
        string successMessage,
        Func<int, Task<StockCountSessionDto>> action)
    {
        if (SelectedSession is null)
        {
            return;
        }

        var id = SelectedSession.Id;
        IsActionPending = true;
        ActionFeedback = null;
        ErrorMessage = null;
        try
        {
            await action(id);
            ActionFeedback = successMessage;
            await LoadSessionsAsync();
            await LoadDetailAsync(id);
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo en la acción de conteo: {ex.Message}", nameof(StockCountViewModel));
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsActionPending = false;
        }
    }

    private async Task LoadSessionsAsync()
    {
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var token = newCts.Token;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var sessions = await _stockCountService.ListAsync(SelectedStatusOption.Value, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var selectedId = SelectedSession?.Id;
            var rows = sessions.Select(session => new StockCountSessionRowViewModel(session)).ToList();
            void Apply()
            {
                Sessions.Clear();
                foreach (var row in rows)
                {
                    Sessions.Add(row);
                }

                SelectedSession = selectedId.HasValue
                    ? Sessions.FirstOrDefault(session => session.Id == selectedId.Value)
                    : null;
            }

            InvokeOnUi(Apply);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al cargar sesiones de conteo: {ex.Message}", nameof(StockCountViewModel));
            ErrorMessage = $"Error al cargar las sesiones: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    private async Task LoadDetailAsync(int id)
    {
        IsLoadingDetail = true;
        ErrorMessage = null;
        try
        {
            var detail = await _stockCountService.GetDetailAsync(id);
            var row = Sessions.FirstOrDefault(session => session.Id == id);
            row?.Update(detail.Session);
            if (row is not null)
            {
                SelectedSession = row;
            }

            Items.Clear();
            foreach (var item in detail.Items)
            {
                Items.Add(new StockCountItemRowViewModel(item));
            }

            if (CanManage)
            {
                await LoadReviewAsync(id);
            }
            else
            {
                Review = null;
                ReviewItems.Clear();
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al cargar el detalle de conteo: {ex.Message}", nameof(StockCountViewModel));
            ErrorMessage = $"Error al cargar el detalle: {ex.Message}";
        }
        finally
        {
            IsLoadingDetail = false;
            NotifyStateChanged();
        }
    }

    private async Task LoadReviewAsync(int id)
    {
        if (!CanManage)
        {
            return;
        }

        IsReviewLoading = true;
        try
        {
            var review = await _stockCountService.GetReviewAsync(id);
            ReviewItems.Clear();
            foreach (var item in review.Items)
            {
                ReviewItems.Add(new StockCountReviewItemRowViewModel(item));
            }

            Review = review;
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al cargar el descuadre del conteo: {ex.Message}", nameof(StockCountViewModel));
            ErrorMessage = $"Error al cargar el descuadre: {ex.Message}";
        }
        finally
        {
            IsReviewLoading = false;
        }
    }

    private async Task LoadCategoriesAsync()
    {
        if (_categoryService is null)
        {
            return;
        }

        try
        {
            var categories = await _categoryService.GetCategoriesAsync();
            void Apply()
            {
                Categories.Clear();
                Categories.Add(new StockCountCategoryOption(null, "Todo el inventario"));
                foreach (var category in categories.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
                {
                    Categories.Add(new StockCountCategoryOption(category.Id, category.Path));
                }

                CreateScopeCategory = Categories[0];
            }

            InvokeOnUi(Apply);
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogWarning($"No se pudieron cargar las categorías del conteo: {ex.Message}", nameof(StockCountViewModel));
        }
    }

    private void OnSessionChanged() => NotifyStateChanged();

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasAccess));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(CanCount));
        OnPropertyChanged(nameof(CanCreateSession));
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(SelectedSessionIsOpen));
        OnPropertyChanged(nameof(SelectedSessionIsClosed));
        OnPropertyChanged(nameof(SelectedSessionIsTerminal));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanEditItems));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(ShowReview));
        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasNoSessions));
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasScanFeedback));
        OnPropertyChanged(nameof(HasCreateError));
        OnPropertyChanged(nameof(HasActionFeedback));
        OnPropertyChanged(nameof(ScanFeedbackColor));
    }

    private void InvokeOnUi(Action action)
    {
        if (_dispatcherInvoker.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcherInvoker.Invoke(action);
        }
    }

    private static IReadOnlyList<StockCountStatusOption> CreateStatusOptions() => new[]
    {
        new StockCountStatusOption(null, "Todas"),
        new StockCountStatusOption(StockCountStatus.Open, "Abierta"),
        new StockCountStatusOption(StockCountStatus.Closed, "Cerrada"),
        new StockCountStatusOption(StockCountStatus.Applied, "Aplicada"),
        new StockCountStatusOption(StockCountStatus.Cancelled, "Cancelada")
    };

    private static string FormatQuantity(decimal value)
        => value.ToString("#,##0.###", MoneyFormat.Culture);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (UserSession != null)
        {
            UserSession.SessionChanged -= OnSessionChanged;
        }

        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        _loadCts = null;
        GC.SuppressFinalize(this);
    }
}

public sealed record StockCountStatusOption(StockCountStatus? Value, string Label);

public sealed record StockCountCategoryOption(int? Id, string Label);

/// <summary>Fila del listado de sesiones con displays de estado y alcance.</summary>
public sealed class StockCountSessionRowViewModel : ObservableObject
{
    private StockCountSessionDto _session;

    public StockCountSessionRowViewModel(StockCountSessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public int Id => _session.Id;
    public string Name => _session.Name;
    public StockCountStatus Status => _session.Status;
    public int ItemsCount => _session.ItemsCount;
    public string ScopeDisplay => string.IsNullOrWhiteSpace(_session.ScopeCategoryPath)
        ? "Todo el inventario"
        : _session.ScopeCategoryPath!;
    public string CreatedAtText => FormatLocal(_session.CreatedAt);
    public string StatusLabel => _session.Status switch
    {
        StockCountStatus.Open => "Abierta",
        StockCountStatus.Closed => "Cerrada",
        StockCountStatus.Applied => "Aplicada",
        StockCountStatus.Cancelled => "Cancelada",
        _ => _session.Status.ToString()
    };
    public string StatusColor => _session.Status switch
    {
        StockCountStatus.Open => "#0369A1",
        StockCountStatus.Closed => "#B45309",
        StockCountStatus.Applied => "#047857",
        StockCountStatus.Cancelled => "#B91C1C",
        _ => "#475569"
    };
    public string StatusBackground => _session.Status switch
    {
        StockCountStatus.Open => "#E0F2FE",
        StockCountStatus.Closed => "#FEF3C7",
        StockCountStatus.Applied => "#D1FAE5",
        StockCountStatus.Cancelled => "#FEE2E2",
        _ => "#F1F5F9"
    };
    public string StatusContext
    {
        get
        {
            var stamp = _session.AppliedAt ?? _session.ClosedAt;
            return stamp.HasValue ? FormatLocal(stamp.Value) : string.Empty;
        }
    }

    public void Update(StockCountSessionDto session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ItemsCount));
        OnPropertyChanged(nameof(ScopeDisplay));
        OnPropertyChanged(nameof(CreatedAtText));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusContext));
    }

    private static string FormatLocal(DateTime utc)
        => TimeZoneHelper.ToVenezuelaTime(utc).ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>Ítem ciego: producto + cantidad contada, sin teórico ni costo.</summary>
public sealed class StockCountItemRowViewModel
{
    private readonly StockCountItemDto _item;

    public StockCountItemRowViewModel(StockCountItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _item = item;
    }

    public int ProductId => _item.ProductId;
    public string Sku => _item.SKU;
    public string ProductName => _item.ProductName;
    public decimal CountedQuantity => _item.CountedQuantity;
    public string CountedQuantityText => _item.CountedQuantity.ToString("#,##0.###", MoneyFormat.Culture);
    public string UpdatedAtText => TimeZoneHelper.ToVenezuelaTime(_item.UpdatedAt)
        .ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>Fila del descuadre; solo se construye para Admin/Manager.</summary>
public sealed class StockCountReviewItemRowViewModel
{
    private readonly StockCountReviewItemDto _item;

    public StockCountReviewItemRowViewModel(StockCountReviewItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _item = item;
    }

    public string Sku => _item.SKU;
    public string ProductName => _item.ProductName;
    public string CountedText => FormatQuantity(_item.CountedQuantity);
    public string TheoreticalText => FormatQuantity(_item.TheoreticalStock);
    public string DifferenceText => FormatSigned(_item.Difference);
    public string UnitCostText => FormatMoney(_item.UnitCostUSD);
    public string DifferenceValueText => FormatMoney(_item.DifferenceValueUsd);
    public string DifferenceColor => _item.Difference switch
    {
        > 0m => "#047857",
        < 0m => "#B91C1C",
        _ => "#475569"
    };

    private static string FormatQuantity(decimal value)
        => value.ToString("#,##0.###", MoneyFormat.Culture);

    private static string FormatSigned(decimal value)
        => value.ToString("+#,##0.###;-#,##0.###;0", MoneyFormat.Culture);

    private static string FormatMoney(decimal value)
        => $"{CurrencyDisplay.PrimaryCurrencySymbol}{MoneyFormat.N2(value)}";
}
