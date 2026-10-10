using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>8.150 (T10, design D7): solicitud pendiente en la cola admin del WPF.</summary>
public sealed class AuthorizationNotificationItem
{
    public int RequestId { get; init; }

    public string? ActionType { get; init; }

    public int? SaleId { get; init; }

    public string? RequestedByName { get; init; }

    public string? Terminal { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Mensaje enriquecido ya armado (plantilla D7) al momento de encolar.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 8.150 (T10, design D7): cola de notificaciones admin (Admin/Manager) del WPF, paridad con T8
/// Web. Escucha los eventos del hub, muestra una solicitud a la vez con badge de pendientes,
/// resuelve con aprobar/rechazar + motivo opcional y cierra silenciosamente cuando la resolucion
/// es propia. Los avisos de carrera/expiracion son el mensaje exacto de la spec, prefiriendo el
/// mensaje del servidor. Todos los cambios de estado se marshalean al dispatcher de UI.
/// </summary>
public partial class AuthorizationNotificationViewModel : ObservableObject, IDisposable
{
    public const string TitleMessage = "Solicitud de autorización";
    public const string ApproveMessage = "Aprobar";
    public const string RejectMessage = "Rechazar";
    public const string ReasonLabel = "Motivo (opcional)";
    public const string TerminalLabel = "Terminal";
    public const string RemainingTimeLabel = "Tiempo restante";
    public const string ExpiredNoticeMessage = "Solicitud expirada.";
    public const string PendingCountLabel = "solicitudes pendientes";
    public const string DismissMessage = "Cerrar";
    public const string ResolveFailedMessage = "No se pudo resolver la solicitud de autorización.";
    public const string FallbackResolverName = "otro usuario";
    public const string DefaultActionLabel = "una acción protegida";
    public const string ManualPriceOverrideActionLabel = "modificar el precio manual";

    internal static readonly TimeSpan CountdownInterval = TimeSpan.FromMilliseconds(500);

    private readonly IAuthorizationHubService _hubService;
    private readonly IDispatcherInvoker _dispatcherInvoker;
    private readonly IAuthorizationCountdownTimer _countdownTimer;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly HashSet<int> _ownResolvedRequestIds = new();

    private int _pendingResolutionRequestId;
    private int _isActive;
    private int _isDisposed;

    public AuthorizationNotificationViewModel(
        IAuthorizationHubService hubService,
        IDispatcherInvoker? dispatcherInvoker = null,
        IAuthorizationCountdownTimer? countdownTimer = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(hubService);

        _hubService = hubService;
        _dispatcherInvoker = dispatcherInvoker ?? new InlineDispatcherInvoker();
        _countdownTimer = countdownTimer ?? new ThreadingAuthorizationCountdownTimer();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Cambios estructurales de cola/aviso; lo usa el host para abrir/cerrar el modal.</summary>
    public event EventHandler? StateChanged;

    public ObservableCollection<AuthorizationNotificationItem> PendingRequests { get; } = new();

    public bool IsActive => Volatile.Read(ref _isActive) != 0;

    public bool HasPendingNotifications => PendingRequests.Count > 0;

    public int QueuedCount => PendingRequests.Count;

    public bool ShowQueueBadge => PendingRequests.Count > 1;

    public string QueueBadgeText => $"{PendingRequests.Count} {PendingCountLabel}";

    public AuthorizationNotificationItem? CurrentRequest => PendingRequests.Count > 0 ? PendingRequests[0] : null;

    public string CurrentMessage => CurrentRequest?.Message ?? string.Empty;

    public string CurrentTerminalDisplay => CurrentRequest?.Terminal ?? string.Empty;

    public string RemainingTimeDisplay => $"{RemainingSeconds / 60:00}:{RemainingSeconds % 60:00}";

    public bool ShowDismissButton => !HasPendingNotifications && Notice != null;

    public bool ShowResolutionPanel => HasPendingNotifications;

    [ObservableProperty]
    private int _remainingSeconds;

    [ObservableProperty]
    private string _reason = string.Empty;

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private string _resolveError = string.Empty;

    [ObservableProperty]
    private bool _isSubmitting;

    /// <summary>8.150 (T10): activa la escucha del hub solo durante sesiones elevadas.</summary>
    public void Activate()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _isActive, 1) != 0)
        {
            return;
        }

        _hubService.AuthorizationRequested += OnRequested;
        _hubService.AuthorizationResolved += OnResolved;
        _hubService.AuthorizationExpired += OnExpired;
    }

    /// <summary>8.150 (T10): desactiva la escucha y descarta la cola (p. ej. logout o cambio de rol).</summary>
    public void Deactivate()
    {
        if (Interlocked.Exchange(ref _isActive, 0) == 0)
        {
            return;
        }

        _hubService.AuthorizationRequested -= OnRequested;
        _hubService.AuthorizationResolved -= OnResolved;
        _hubService.AuthorizationExpired -= OnExpired;

        _countdownTimer.Stop();
        PendingRequests.Clear();
        Volatile.Write(ref _pendingResolutionRequestId, 0);
        _ownResolvedRequestIds.Clear();
        Notice = null;
        ResolveError = string.Empty;
        Reason = string.Empty;
        RemainingSeconds = 0;
        RaiseHeadProperties();
        RaiseStateChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        Deactivate();
        _countdownTimer.Dispose();
        StateChanged = null;
    }

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private Task ApproveAsync() => ResolveCurrentAsync(approved: true, reason: null);

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private Task RejectAsync() => ResolveCurrentAsync(approved: false, reason: NormalizeReason());

    [RelayCommand(CanExecute = nameof(CanDismissNotice))]
    private void DismissNotice() => Notice = null;

    private bool CanResolve => HasPendingNotifications && !IsSubmitting;

    private bool CanDismissNotice => Notice != null;

    /// <summary>8.150 (T10): mensaje D7 exacto con cajero, accion, factura y detalle del contexto.</summary>
    public static string BuildNotificationMessage(AuthorizationRequestedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var cashier = string.IsNullOrWhiteSpace(payload.RequestedByName) ? null : payload.RequestedByName;
        var subject = cashier is null ? "Un cajero" : $"El cajero {cashier}";
        var action = BuildActionLabel(payload.ActionType);
        var saleClause = payload.SaleId.HasValue ? $" en la Factura #{payload.SaleId.Value}" : string.Empty;
        var baseMessage = $"{subject} solicita autorización para {action}{saleClause}.";
        var detail = BuildContextDetail(payload.Context);
        return detail.Length == 0 ? baseMessage : $"{baseMessage} {detail}";
    }

    /// <summary>8.150 (T10, spec S4): formato exacto "Esta solicitud ya fue resuelta por {resolverName}.".</summary>
    public static string BuildAlreadyResolvedMessage(string? resolverName)
        => $"Esta solicitud ya fue resuelta por {(string.IsNullOrWhiteSpace(resolverName) ? FallbackResolverName : resolverName)}.";

    public static string BuildActionLabel(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return DefaultActionLabel;
        }

        return string.Equals(actionType, "ManualPriceOverride", StringComparison.OrdinalIgnoreCase)
            ? ManualPriceOverrideActionLabel
            : actionType;
    }

    /// <summary>8.150 (T10): detalle de presentacion del contexto (nunca se hashea, design D3).</summary>
    public static string BuildContextDetail(JsonElement? context)
    {
        if (context is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (TryGetString(element, "productName", out var productName) && !string.IsNullOrWhiteSpace(productName))
        {
            var quantitySuffix = TryGetDecimal(element, "quantity", out var quantity)
                ? $" × {quantity.ToString("0.###", CultureInfo.InvariantCulture)}"
                : string.Empty;
            parts.Add($"Producto: {productName}{quantitySuffix}");
        }

        if (TryGetDecimal(element, "customUnitPriceUsd", out var usd))
        {
            parts.Add($"Precio USD: $ {usd.ToString("0.00", CultureInfo.InvariantCulture)}");
        }

        if (TryGetDecimal(element, "customUnitPriceLocal", out var local))
        {
            parts.Add($"Precio Bs.S: {local.ToString("0.00", CultureInfo.InvariantCulture)}");
        }

        return string.Join(". ", parts);
    }

    private async Task ResolveCurrentAsync(bool approved, string? reason)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Volatile.Read(ref _isActive) == 0)
        {
            return;
        }

        if (PendingRequests.Count == 0 || IsSubmitting)
        {
            return;
        }

        var requestId = PendingRequests[0].RequestId;
        Volatile.Write(ref _pendingResolutionRequestId, requestId);
        IsSubmitting = true;
        ResolveError = string.Empty;

        try
        {
            await _hubService.ResolveAuthorizationAsync(requestId, approved, reason);
            // Guard de resolucion propia (paridad Web ownResolvedRef): si el push del hub llega
            // despues de la respuesta, no debe mostrarse el aviso de carrera al propio resolutor.
            _ownResolvedRequestIds.Add(requestId);
            RemoveRequest(requestId);
        }
        catch (AuthorizationHubException ex) when (ex.StatusCode is 409 or 404)
        {
            Notice = string.IsNullOrWhiteSpace(ex.Message) ? ResolveFailedMessage : ex.Message;
            RemoveRequest(requestId);
        }
        catch (Exception ex)
        {
            ResolveError = string.IsNullOrWhiteSpace(ex.Message) ? ResolveFailedMessage : ex.Message;
        }
        finally
        {
            Volatile.Write(ref _pendingResolutionRequestId, 0);
            IsSubmitting = false;
        }
    }

    private string? NormalizeReason()
    {
        var trimmed = (Reason ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private void OnRequested(object? sender, AuthorizationRequestedPayload payload)
        => _dispatcherInvoker.BeginInvoke(() => ApplyRequested(payload));

    private void ApplyRequested(AuthorizationRequestedPayload payload)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Volatile.Read(ref _isActive) == 0)
        {
            return;
        }

        if (payload.RequestId <= 0 || PendingRequests.Any(item => item.RequestId == payload.RequestId))
        {
            return;
        }

        var wasEmpty = PendingRequests.Count == 0;
        PendingRequests.Add(new AuthorizationNotificationItem
        {
            RequestId = payload.RequestId,
            ActionType = payload.ActionType,
            SaleId = payload.SaleId,
            RequestedByName = payload.RequestedByName,
            Terminal = payload.Terminal,
            CreatedAt = payload.CreatedAt,
            ExpiresAt = payload.ExpiresAt,
            Message = BuildNotificationMessage(payload)
        });

        if (wasEmpty)
        {
            Reason = string.Empty;
            ResolveError = string.Empty;
            _countdownTimer.Start(CountdownInterval, OnCountdownTick);
        }

        RefreshCountdown();
        RaiseHeadProperties();
        RaiseStateChanged();
    }

    private void OnResolved(object? sender, AuthorizationResolvedPayload payload)
        => _dispatcherInvoker.BeginInvoke(() => ApplyResolved(payload));

    private void ApplyResolved(AuthorizationResolvedPayload payload)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Volatile.Read(ref _isActive) == 0)
        {
            return;
        }

        var requestId = payload.RequestId;
        var isOwnResolution = Volatile.Read(ref _pendingResolutionRequestId) == requestId
            || _ownResolvedRequestIds.Remove(requestId);
        if (!PendingRequests.Any(item => item.RequestId == requestId))
        {
            return;
        }

        // El aviso debe existir ANTES de que la cola quede vacia: el host cierra el modal cuando
        // no hay pendientes y no hay aviso, y el StateChanged de la remocion es el que dispara ese
        // cierre (si el aviso se asigna despues, el mensaje de carrera/expired nunca se muestra).
        if (!isOwnResolution)
        {
            Notice = BuildAlreadyResolvedMessage(payload.ResolvedByName);
        }
        RemoveRequest(requestId);
    }

    private void OnExpired(object? sender, AuthorizationExpiredPayload payload)
        => _dispatcherInvoker.BeginInvoke(() => ApplyExpired(payload));

    private void ApplyExpired(AuthorizationExpiredPayload payload)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || Volatile.Read(ref _isActive) == 0)
        {
            return;
        }

        if (!PendingRequests.Any(item => item.RequestId == payload.RequestId))
        {
            return;
        }

        Notice = ExpiredNoticeMessage;
        RemoveRequest(payload.RequestId);
    }

    private void OnCountdownTick()
    {
        _dispatcherInvoker.BeginInvoke(() =>
        {
            if (Volatile.Read(ref _isDisposed) != 0)
            {
                return;
            }

            DropExpired();
            RefreshCountdown();
        });
    }

    private void RefreshCountdown()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        if (PendingRequests.Count == 0)
        {
            RemainingSeconds = 0;
            return;
        }

        var expiresAt = PendingRequests[0].ExpiresAt;
        RemainingSeconds = expiresAt.HasValue
            ? Math.Max(0, (int)Math.Ceiling((expiresAt.Value - _utcNow()).TotalSeconds))
            : 0;
    }

    /// <summary>8.150 (T10): la expiracion local retira la solicitud vencida con el aviso breve.</summary>
    private void DropExpired()
    {
        var now = _utcNow();
        var removed = false;
        for (var i = PendingRequests.Count - 1; i >= 0; i--)
        {
            var item = PendingRequests[i];
            if (item.ExpiresAt.HasValue && item.ExpiresAt.Value <= now)
            {
                PendingRequests.RemoveAt(i);
                removed = true;
            }
        }

        if (!removed)
        {
            return;
        }

        if (PendingRequests.Count == 0)
        {
            _countdownTimer.Stop();
            RemainingSeconds = 0;
        }
        else
        {
            Reason = string.Empty;
            ResolveError = string.Empty;
        }

        Notice = ExpiredNoticeMessage;
        RaiseHeadProperties();
        RaiseStateChanged();
    }

    private void RemoveRequest(int requestId)
    {
        var index = -1;
        for (var i = 0; i < PendingRequests.Count; i++)
        {
            if (PendingRequests[i].RequestId == requestId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var wasHead = index == 0;
        PendingRequests.RemoveAt(index);

        if (PendingRequests.Count == 0)
        {
            _countdownTimer.Stop();
            RemainingSeconds = 0;
        }
        else if (wasHead)
        {
            Reason = string.Empty;
            ResolveError = string.Empty;
            RefreshCountdown();
        }

        RaiseHeadProperties();
        RaiseStateChanged();
    }

    private void RaiseHeadProperties()
    {
        OnPropertyChanged(nameof(CurrentRequest));
        OnPropertyChanged(nameof(CurrentMessage));
        OnPropertyChanged(nameof(CurrentTerminalDisplay));
        OnPropertyChanged(nameof(HasPendingNotifications));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(ShowQueueBadge));
        OnPropertyChanged(nameof(QueueBadgeText));
        OnPropertyChanged(nameof(ShowDismissButton));
        OnPropertyChanged(nameof(ShowResolutionPanel));
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    partial void OnRemainingSecondsChanged(int value) => OnPropertyChanged(nameof(RemainingTimeDisplay));

    partial void OnNoticeChanged(string? value)
    {
        DismissNoticeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowDismissButton));
        RaiseStateChanged();
    }

    partial void OnIsSubmittingChanged(bool value)
    {
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        return false;
    }

    private static bool TryGetDecimal(JsonElement element, string name, out decimal value)
    {
        value = 0m;
        if (!TryGetProperty(element, name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String
            && decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        // El notificador del backend emite camelCase; se acepta PascalCase por robustez.
        var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
        if (element.TryGetProperty(pascalName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
