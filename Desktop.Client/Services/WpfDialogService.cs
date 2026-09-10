using System;
using System.Windows;
using Core.Common;
using Core.DTOs;
using Core.Logging;
using Desktop.Client.Views;
using Microsoft.Extensions.Logging;

namespace Desktop.Client.Services;

public partial class WpfDialogService : IDialogService
{
    private readonly IClientStateService _clientState;
    private readonly ISalesService _salesService;
    private readonly IProductService? _productService;
    private readonly ILogger<WpfDialogService>? _logger;

    // Contador de diálogos modales abiertos (ventanas ShowDialog y DialogHost). Todos los
    // diálogos se abren desde esta clase, así que este contador cubre la app completa.
    private int _openModalCount;

    /// <summary>true si hay un diálogo modal abierto (ventana ShowDialog o DialogHost).</summary>
    public bool HasOpenModalDialog => _openModalCount > 0;

    private IDisposable TrackModal()
    {
        _openModalCount++;
        return new ModalScope(this);
    }

    private sealed class ModalScope : IDisposable
    {
        private readonly WpfDialogService _owner;
        public ModalScope(WpfDialogService owner) => _owner = owner;
        public void Dispose() => _owner._openModalCount--;
    }

    private readonly IConnectionManager? _connectionManager;
    private readonly ISubnetScannerService? _scannerService;
    private readonly System.Net.Http.IHttpClientFactory? _httpClientFactory;
    private readonly IExchangeRateService? _exchangeRateService;

    public WpfDialogService(
        IClientStateService clientState, 
        ISalesService? salesService = null, 
        IProductService? productService = null, 
        ILogger<WpfDialogService>? logger = null,
        IConnectionManager? connectionManager = null,
        ISubnetScannerService? scannerService = null,
        System.Net.Http.IHttpClientFactory? httpClientFactory = null,
        IExchangeRateService? exchangeRateService = null)
    {
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _salesService = salesService!;
        _productService = productService;
        _logger = logger;
        _connectionManager = connectionManager;
        _scannerService = scannerService;
        _httpClientFactory = httpClientFactory;
        _exchangeRateService = exchangeRateService;
    }


    public bool ShowConfirm(string title, string message)
    {
        if (Application.Current == null)
        {
            // Seguridad Extrema / Denegación por Defecto:
            // Sin contexto UI no es posible obtener confirmación humana del cajero.
            string logMessage = $"[NO-OP DIALOG SUPPRESSED - ACTION DENIED] Application.Current es nulo en ShowConfirm. Operación denegada automáticamente: {title} - {message}";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return false;
        }

        if (_clientState.IsFatalErrorActive)
        {
            // Política de Denegación Estricta sin Excepciones / Defensa en Profundidad:
            // Blinda el sistema frente a operaciones destructivas que no dependen de HTTP.
            // En estado degradado se bloquea EN RAÍZ cualquier acción que pida confirmación humana
            // para evitar malinterpretaciones del cajero; el diálogo no se renderiza y retorna false.
            string logMessage = $"[CIRCUIT BREAKER - ACTION DENIED] Cortacircuitos activo en ShowConfirm. Operación denegada de raíz: {title} - {message}";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return false;
        }

        using var _ = TrackModal();
        bool? dialogResult = false;
        if (Application.Current.Dispatcher.CheckAccess())
        {
            var dialog = new CustomDialogWindow(title, message, CustomDialogType.Confirm);
            dialogResult = dialog.ShowDialog();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dialog = new CustomDialogWindow(title, message, CustomDialogType.Confirm);
                dialogResult = dialog.ShowDialog();
            });
        }

        return dialogResult == true;
    }

    public void ShowError(string title, string message)
    {
        ShowNotificationDialog(title, message, CustomDialogType.Error, "ShowError");
    }

    public void ShowWarning(string title, string message)
    {
        ShowNotificationDialog(title, message, CustomDialogType.Warning, "ShowWarning");
    }

    public void ShowInfo(string title, string message)
    {
        ShowNotificationDialog(title, message, CustomDialogType.Info, "ShowInfo");
    }

    private void ShowNotificationDialog(string title, string message, CustomDialogType type, string methodName)
    {
        if (Application.Current == null)
        {
            string logMessage = $"[NO-OP DIALOG SUPPRESSED] Application.Current es nulo en {methodName}. Diálogo suprimido: {title} - {message}";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return;
        }

        if (_clientState.IsFatalErrorActive)
        {
            string logMessage = $"[CIRCUIT BREAKER - DIALOG SUPPRESSED] Cortacircuitos activo en {methodName}. Diálogo suprimido: {title} - {message}";
            _logger?.LogWarning(logMessage);
            ClientStateLogger.LogWarning(logMessage);
            return;
        }

        using var _ = TrackModal();
        if (Application.Current.Dispatcher.CheckAccess())
        {
            var dialog = new CustomDialogWindow(title, message, type);
            dialog.ShowDialog();
        }
        else
        {
            // 8.9-B7: los diálogos informativos no bloquean el hilo de llamada (servicio/polling);
            // se envían al hilo UI en async y se reportan fallos vía SafeFireAndForget.
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new CustomDialogWindow(title, message, type);
                dialog.ShowDialog();
            }).Task.SafeFireAndForget($"WpfDialogService.{methodName}");
        }
    }
}
