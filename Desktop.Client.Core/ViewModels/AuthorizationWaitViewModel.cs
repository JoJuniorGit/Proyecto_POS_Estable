using System.Globalization;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Desktop.Client.Services;
using Sales.Module.DTOs;

namespace Desktop.Client.ViewModels;

/// <summary>8.150 (T9, design D7): fases del dialogo de espera bloqueante del cajero.</summary>
public enum AuthorizationWaitPhase
{
    Idle,
    Waiting,
    Granted,
    Rejected,
    Expired
}

/// <summary>
/// 8.150 (T9): temporizador inyectable del countdown; en produccion late con System.Threading.Timer
/// y en pruebas se reemplaza por un fake para no depender de relojes reales.
/// </summary>
public interface IAuthorizationCountdownTimer : IDisposable
{
    void Start(TimeSpan interval, Action onTick);

    void Stop();
}

/// <summary>
/// 8.150 (T9, design D7): estado del dialogo de espera. Crea la solicitud por el hub, escucha la
/// resolucion/expresion, ofrece autorizacion local y reanuda la accion protegida con el token una
/// sola vez (guard de ejecucion unica), degradando aprobaciones sin token a "Expirada".
/// </summary>
public partial class AuthorizationWaitViewModel : ObservableObject, IDisposable
{
    public const string WaitingMessage = "Esperando autorización remota...";
    public const string RejectedMessage = "Solicitud rechazada.";
    public const string ExpiredMessage = "Expirada";
    public const string LocalAuthorizationMessage = "Autorización Local";
    public const string RetryMessage = "Reintentar";
    public const string CancelMessage = "Cancelar";
    public const string AuthorizeMessage = "Autorizar";
    public const string UsernameLabel = "Usuario";
    public const string PasswordLabel = "Contraseña";
    public const string ExecutionFailedMessage = "No se pudo completar la operación autorizada.";

    internal static readonly TimeSpan CountdownInterval = TimeSpan.FromMilliseconds(500);

    private readonly IAuthorizationHubService _hubService;
    private readonly AuthorizationRequestContext _context;
    private readonly Func<string, Task> _retryAction;
    private readonly IDispatcherInvoker _dispatcherInvoker;
    private readonly IAuthorizationCountdownTimer _countdownTimer;
    private readonly Func<DateTimeOffset> _utcNow;

    private int _requestId;
    private DateTimeOffset _expiresAtUtc;
    private string? _grantToken;
    private string _grantResolutionMode = "Remote";
    private string? _grantResolvedByName;
    private int _isExecuting;
    private int _isDisposed;

    public Action<bool>? RequestClose;

    [ObservableProperty]
    private AuthorizationWaitPhase _phase = AuthorizationWaitPhase.Idle;

    [ObservableProperty]
    private int _remainingSeconds;

    [ObservableProperty]
    private bool _isLocalFormOpen;

    [ObservableProperty]
    private string _localUsername = string.Empty;

    [ObservableProperty]
    private string _localPassword = string.Empty;

    [ObservableProperty]
    private string _localError = string.Empty;

    [ObservableProperty]
    private bool _isSubmittingLocal;

    [ObservableProperty]
    private string _retryError = string.Empty;

    [ObservableProperty]
    private string? _rejectionReason;

    public AuthorizationWaitViewModel(
        IAuthorizationHubService hubService,
        AuthorizationRequestContext context,
        Func<string, Task> retryAction,
        IDispatcherInvoker? dispatcherInvoker = null,
        IAuthorizationCountdownTimer? countdownTimer = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(hubService);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(retryAction);

        _hubService = hubService;
        _context = context;
        _retryAction = retryAction;
        _dispatcherInvoker = dispatcherInvoker ?? new InlineDispatcherInvoker();
        _countdownTimer = countdownTimer ?? new ThreadingAuthorizationCountdownTimer();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        _hubService.AuthorizationResolved += OnAuthorizationResolved;
        _hubService.AuthorizationExpired += OnAuthorizationExpired;
    }

    public AuthorizationRequestContext OperationContext => _context;

    public string OperationSummary => BuildOperationSummary(_context);

    public string SaleNumberDisplay => $"#{_context.SaleId}";

    public string ProductDisplay => string.IsNullOrWhiteSpace(_context.ProductName)
        ? string.Empty
        : $"{_context.ProductName} × {_context.Quantity.ToString("0.###", CultureInfo.InvariantCulture)}";

    public string CustomPriceUsdDisplay => _context.CustomUnitPriceUsd.HasValue
        ? $"$ {_context.CustomUnitPriceUsd.Value.ToString("N2", CultureInfo.InvariantCulture)}"
        : string.Empty;

    public string CustomPriceLocalDisplay => _context.CustomUnitPriceLocal.HasValue
        ? $"Bs.S {_context.CustomUnitPriceLocal.Value.ToString("N2", CultureInfo.InvariantCulture)}"
        : string.Empty;

    public string StatusMessage => Phase switch
    {
        AuthorizationWaitPhase.Waiting => WaitingMessage,
        AuthorizationWaitPhase.Rejected => RejectedMessage,
        AuthorizationWaitPhase.Expired => ExpiredMessage,
        AuthorizationWaitPhase.Granted => "Autorización concedida.",
        _ => string.Empty
    };

    public string RemainingTimeDisplay => $"{RemainingSeconds / 60:00}:{RemainingSeconds % 60:00}";

    public bool IsWaiting => Phase == AuthorizationWaitPhase.Waiting;
    public bool IsRejected => Phase == AuthorizationWaitPhase.Rejected;
    public bool IsExpired => Phase == AuthorizationWaitPhase.Expired;
    public bool IsGranted => Phase == AuthorizationWaitPhase.Granted;
    public bool ShowWaitingPanel => Phase == AuthorizationWaitPhase.Waiting && !IsLocalFormOpen;
    public bool ShowLocalFormPanel => Phase == AuthorizationWaitPhase.Waiting && IsLocalFormOpen;
    public bool ShowRetryExecutionPanel => Phase == AuthorizationWaitPhase.Granted && !string.IsNullOrEmpty(RetryError);
    public bool ShowExecutionProgress => Phase == AuthorizationWaitPhase.Granted && string.IsNullOrEmpty(RetryError);

    public static string BuildOperationSummary(AuthorizationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var product = string.IsNullOrWhiteSpace(context.ProductName) ? "Producto" : context.ProductName;
        return $"{product} × {context.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} - Venta #{context.SaleId}";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Phase != AuthorizationWaitPhase.Idle)
        {
            return;
        }

        var request = await _hubService.RequestAuthorizationAsync(_context);
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        BeginWaiting(request);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        // 8.151 (W3, R4-client): el retiro server-side solo aplica mientras la solicitud sigue
        // Pending; es best-effort y un fallo (409 de carrera, red) no bloquea el cierre local:
        // la solicitud expira sola.
        if (Phase == AuthorizationWaitPhase.Waiting && _requestId > 0)
        {
            _hubService.CancelRequestAsync(_requestId).SafeFireAndForget("AuthorizationWaitViewModel.CancelServerRequest");
        }

        StopCountdown();
        IsLocalFormOpen = false;
        Phase = AuthorizationWaitPhase.Idle;
        RequestClose?.Invoke(false);
    }

    private bool CanCancel => Phase is AuthorizationWaitPhase.Waiting or AuthorizationWaitPhase.Rejected or AuthorizationWaitPhase.Expired
        || (Phase == AuthorizationWaitPhase.Granted && !string.IsNullOrEmpty(RetryError));

    [RelayCommand(CanExecute = nameof(CanOpenLocalForm))]
    private void OpenLocalAuthorization()
    {
        LocalError = string.Empty;
        IsLocalFormOpen = true;
    }

    private bool CanOpenLocalForm => Phase == AuthorizationWaitPhase.Waiting;

    [RelayCommand(CanExecute = nameof(CanCloseLocalForm))]
    private void CloseLocalForm()
    {
        LocalError = string.Empty;
        IsLocalFormOpen = false;
    }

    private bool CanCloseLocalForm => IsLocalFormOpen;

    [RelayCommand(CanExecute = nameof(CanSubmitLocal))]
    private async Task SubmitLocalAuthorizationAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Phase != AuthorizationWaitPhase.Waiting)
        {
            return;
        }

        var username = LocalUsername.Trim();
        var password = LocalPassword;
        if (username.Length == 0 || password.Length == 0)
        {
            LocalError = AuthorizationMessages.InvalidCredentials;
            return;
        }

        IsSubmittingLocal = true;
        LocalError = string.Empty;
        try
        {
            var result = await _hubService.LocalResolveAsync(_requestId, username, password, reason: null);
            if (Volatile.Read(ref _isDisposed) != 0)
            {
                return;
            }

            LocalPassword = string.Empty;
            IsLocalFormOpen = false;
            await ExecuteRetryAsync(result.Token, "Local", result.SupervisorName);
        }
        catch (AuthorizationHubException ex) when (ex.StatusCode == 409)
        {
            var recovered = await RecoverStatusAsync();
            if (!recovered && Volatile.Read(ref _isDisposed) == 0)
            {
                LocalError = string.IsNullOrWhiteSpace(ex.Message) ? AuthorizationMessages.InvalidCredentials : ex.Message;
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _isDisposed) == 0)
            {
                LocalError = string.IsNullOrWhiteSpace(ex.Message) ? AuthorizationMessages.InvalidCredentials : ex.Message;
            }
        }
        finally
        {
            IsSubmittingLocal = false;
        }
    }

    private bool CanSubmitLocal => Phase == AuthorizationWaitPhase.Waiting && !IsSubmittingLocal;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Phase != AuthorizationWaitPhase.Expired)
        {
            return;
        }

        RetryError = string.Empty;
        try
        {
            var request = await _hubService.RequestAuthorizationAsync(_context);
            if (Volatile.Read(ref _isDisposed) != 0)
            {
                return;
            }

            BeginWaiting(request);
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _isDisposed) == 0)
            {
                RetryError = string.IsNullOrWhiteSpace(ex.Message) ? ExecutionFailedMessage : ex.Message;
            }
        }
    }

    private bool CanRetry => Phase == AuthorizationWaitPhase.Expired;

    [RelayCommand(CanExecute = nameof(CanRetryExecution))]
    private Task RetryExecutionAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || string.IsNullOrWhiteSpace(_grantToken))
        {
            return Task.CompletedTask;
        }

        return ExecuteRetryAsync(_grantToken, _grantResolutionMode, _grantResolvedByName);
    }

    private bool CanRetryExecution => Phase == AuthorizationWaitPhase.Granted && !string.IsNullOrEmpty(RetryError);

    /// <summary>
    /// 8.150 (T9): sincroniza el estado por REST (409 de local-resolve o push perdido) sin esperar
    /// al countdown; devuelve false si no pudo consultar o el estado no cambio la fase.
    /// </summary>
    public async Task<bool> RecoverStatusAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Phase != AuthorizationWaitPhase.Waiting || _requestId <= 0)
        {
            return false;
        }

        AuthorizationStatusInfo? status;
        try
        {
            status = await _hubService.GetStatusAsync(_requestId);
        }
        catch (Exception)
        {
            return false;
        }

        if (Volatile.Read(ref _isDisposed) != 0 || status is null || Phase != AuthorizationWaitPhase.Waiting)
        {
            return false;
        }

        if (string.Equals(status.Status, "Approved", StringComparison.OrdinalIgnoreCase))
        {
            StopCountdown();
            IsLocalFormOpen = false;
            await ExecuteRetryAsync(status.Token, status.ResolutionMode ?? "Remote", status.ResolvedByName);
            return true;
        }

        if (string.Equals(status.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            StopCountdown();
            IsLocalFormOpen = false;
            RejectionReason = status.Reason;
            Phase = AuthorizationWaitPhase.Rejected;
            return true;
        }

        if (string.Equals(status.Status, "Expired", StringComparison.OrdinalIgnoreCase)
            || (status.ExpiresAt.HasValue && status.ExpiresAt.Value <= _utcNow()))
        {
            ApplyExpiry();
            return true;
        }

        if (status.ExpiresAt.HasValue)
        {
            _expiresAtUtc = status.ExpiresAt.Value;
            RefreshCountdown();
            return true;
        }

        return false;
    }

    /// <summary>8.150 (T9): recalcula el countdown con el reloj inyectado; al llegar a cero expira.</summary>
    public void RefreshCountdown()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Phase != AuthorizationWaitPhase.Waiting)
        {
            return;
        }

        // 8.151 (W3, R7/design D4c): piso, nunca techo: el display no muestra 00:01 con <1s vivo.
        var remaining = (_expiresAtUtc - _utcNow()).TotalSeconds;
        RemainingSeconds = remaining <= 0 ? 0 : (int)Math.Floor(remaining);
        if (RemainingSeconds <= 0)
        {
            ApplyExpiry();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _hubService.AuthorizationResolved -= OnAuthorizationResolved;
        _hubService.AuthorizationExpired -= OnAuthorizationExpired;
        _countdownTimer.Dispose();
    }

    private void BeginWaiting(AuthorizationRequestInfo request)
    {
        _requestId = request.RequestId;
        _expiresAtUtc = request.ExpiresAt;
        RejectionReason = null;
        LocalError = string.Empty;
        RetryError = string.Empty;
        IsLocalFormOpen = false;
        Phase = AuthorizationWaitPhase.Waiting;
        RefreshCountdown();

        if (Phase == AuthorizationWaitPhase.Waiting)
        {
            _countdownTimer.Start(CountdownInterval, OnCountdownTick);
        }
    }

    private void OnCountdownTick()
    {
        _dispatcherInvoker.BeginInvoke(RefreshCountdown);
    }

    private void StopCountdown()
    {
        _countdownTimer.Stop();
    }

    private void ApplyExpiry()
    {
        if (Phase != AuthorizationWaitPhase.Waiting)
        {
            return;
        }

        StopCountdown();
        IsLocalFormOpen = false;
        Phase = AuthorizationWaitPhase.Expired;
    }

    private async Task ExecuteRetryAsync(string? token, string resolutionMode, string? resolvedByName)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            ApplyExpiry();
            return;
        }

        if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
        {
            return;
        }

        _grantToken = token;
        _grantResolutionMode = resolutionMode;
        _grantResolvedByName = resolvedByName;
        Phase = AuthorizationWaitPhase.Granted;
        RetryError = string.Empty;

        try
        {
            await _retryAction(token);
            if (Volatile.Read(ref _isDisposed) == 0 && Phase == AuthorizationWaitPhase.Granted)
            {
                RequestClose?.Invoke(true);
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _isDisposed) == 0)
            {
                RetryError = string.IsNullOrWhiteSpace(ex.Message) ? ExecutionFailedMessage : ex.Message;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isExecuting, 0);
        }
    }

    private void OnAuthorizationResolved(object? sender, AuthorizationResolvedPayload payload)
    {
        _dispatcherInvoker.BeginInvoke(() => ApplyResolved(payload));
    }

    private void ApplyResolved(AuthorizationResolvedPayload payload)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Phase != AuthorizationWaitPhase.Waiting || payload.RequestId != _requestId)
        {
            return;
        }

        var approved = payload.Approved || string.Equals(payload.Status, "Approved", StringComparison.OrdinalIgnoreCase);
        if (!approved)
        {
            StopCountdown();
            IsLocalFormOpen = false;
            LocalError = string.Empty;
            RejectionReason = payload.Reason;
            Phase = AuthorizationWaitPhase.Rejected;
            return;
        }

        StopCountdown();
        IsLocalFormOpen = false;
        LocalError = string.Empty;
        ExecuteRetryAsync(payload.Token, payload.ResolutionMode ?? "Remote", payload.ResolvedByName)
            .SafeFireAndForget("AuthorizationWaitViewModel.ApplyResolved");
    }

    private void OnAuthorizationExpired(object? sender, AuthorizationExpiredPayload payload)
    {
        _dispatcherInvoker.BeginInvoke(() => ApplyExpired(payload));
    }

    private void ApplyExpired(AuthorizationExpiredPayload payload)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || payload.RequestId != _requestId)
        {
            return;
        }

        ApplyExpiry();
    }

    partial void OnPhaseChanged(AuthorizationWaitPhase value)
    {
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsExpired));
        OnPropertyChanged(nameof(IsGranted));
        OnPropertyChanged(nameof(ShowWaitingPanel));
        OnPropertyChanged(nameof(ShowLocalFormPanel));
        OnPropertyChanged(nameof(ShowRetryExecutionPanel));
        OnPropertyChanged(nameof(ShowExecutionProgress));

        CancelCommand.NotifyCanExecuteChanged();
        OpenLocalAuthorizationCommand.NotifyCanExecuteChanged();
        SubmitLocalAuthorizationCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RetryExecutionCommand.NotifyCanExecuteChanged();
    }

    partial void OnRemainingSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(RemainingTimeDisplay));
    }

    partial void OnIsLocalFormOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWaitingPanel));
        OnPropertyChanged(nameof(ShowLocalFormPanel));
        CloseLocalFormCommand.NotifyCanExecuteChanged();
    }

    partial void OnRetryErrorChanged(string value)
    {
        OnPropertyChanged(nameof(ShowRetryExecutionPanel));
        OnPropertyChanged(nameof(ShowExecutionProgress));
        CancelCommand.NotifyCanExecuteChanged();
        RetryExecutionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSubmittingLocalChanged(bool value)
    {
        SubmitLocalAuthorizationCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// 8.150 (T9): implementacion de produccion del countdown; el tick se marshalea en el VM via
/// IDispatcherInvoker, por lo que aqui solo se programa el reloj del pool.
/// </summary>
internal sealed class ThreadingAuthorizationCountdownTimer : IAuthorizationCountdownTimer
{
    private readonly object _gate = new();
    private Timer? _timer;
    private int _isDisposed;

    public void Start(TimeSpan interval, Action onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);

        lock (_gate)
        {
            if (Volatile.Read(ref _isDisposed) != 0)
            {
                return;
            }

            _timer?.Dispose();
            _timer = new Timer(_ => onTick(), null, interval, interval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        Stop();
    }
}
