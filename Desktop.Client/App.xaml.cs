using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows;
using System.Net.Http;
using System;
using System.Threading.Tasks;

namespace Desktop.Client;

public partial class App : Application
{
    /// <summary>
    /// true cuando el cierre de la aplicación ya está en curso (usuario confirmó, apagado del
    /// sistema o error fatal). Suprime la confirmación de cierre en MainWindow.OnClosing.
    /// </summary>
    public static bool IsShutdownRequested { get; set; }

    /// <summary>
    /// Motivo por el que se inició el cierre de la aplicación (para el log en OnExit):
    /// ventana cerrada, apagado del sistema, error fatal o cierre confirmado con diálogo abierto.
    /// </summary>
    public static string ShutdownReason { get; set; } = string.Empty;

    private IHost? _host;
    private bool _isServicesStopped;
    private readonly string _crashPath = GetCrashPath();

    private static string GetCrashPath()
    {
        try
        {
            // 1. En entorno de desarrollo (con start.bat presente), registrar en la raíz del proyecto
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "start.bat")) || File.Exists(Path.Combine(dir.FullName, "Start.bat")) || File.Exists(Path.Combine(dir.FullName, "scripts", "start.bat")))
                {
                    return Path.Combine(dir.FullName, "crash.txt");
                }
                dir = dir.Parent;
            }

            // 2. En producción, registrar en %LOCALAPPDATA% donde usuarios estándar tienen permisos completos de escritura
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                string logDir = Path.Combine(localAppData, "CommandCenterPOS", "Logs");
                Directory.CreateDirectory(logDir);
                return Path.Combine(logDir, "crash.txt");
            }
        }
        catch
        {
            // Ignorar y proceder al fallback
        }

        // 3. Fallback de emergencia a %TEMP% con ProcessId para evitar colisiones entre instancias concurrentes
        return Path.Combine(Path.GetTempPath(), $"commandcenter_wpf_crash_{Environment.ProcessId}.txt");
    }

    public App()
    {
        try
        {
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));
        }
        catch { }

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        SessionEnding += (s, e) =>
        {
            IsShutdownRequested = true;
            ShutdownReason = "Apagado del sistema operativo (sesión de Windows finalizando)";
        };
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try { File.AppendAllText(_crashPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] UI Exception: " + e.Exception.Message + "\n" + e.Exception.StackTrace + "\n\n"); } catch { }
        e.Handled = true;
        IsShutdownRequested = true;
        ShutdownReason = "Error fatal (excepción de UI)";
        Shutdown();
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            try { File.AppendAllText(_crashPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] AppDomain Exception: " + ex.Message + "\n" + ex.StackTrace + "\n\n"); } catch { }
        }
        try { Environment.Exit(1); } catch { }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { File.AppendAllText(_crashPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Task Exception: " + e.Exception.Message + "\n" + e.Exception.StackTrace + "\n\n"); } catch { }
        e.SetObserved();
    }

    public IHost CreateAndStartHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // 8.6-B5: acota el teardown del host (Dispose best-effort de salida) a 3s máx.
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(opts =>
            opts.ShutdownTimeout = TimeSpan.FromSeconds(3));

        // Service Registration
        builder.Services.AddSingleton<IClientStateService, ClientStateService>();
        builder.Services.AddSingleton<IClientSettingsStore, ClientSettingsStore>();
        builder.Services.AddSingleton<ISubnetScannerService, SubnetScannerService>();
        builder.Services.AddSingleton<IConnectionManager, ConnectionManager>();
        builder.Services.AddSingleton<IDialogService, WpfDialogService>();
        builder.Services.AddSingleton<IDispatcherInvoker, WpfDispatcherInvoker>();
        builder.Services.AddSingleton<IFilePickerDialog, WpfFilePickerDialog>();
        builder.Services.AddSingleton<IAppShutdown, WpfAppShutdown>();
        builder.Services.AddSingleton<IJitterProvider, ProductionJitterProvider>();
        builder.Services.AddSingleton<ISecureTokenStorageService, SecureTokenStorageService>();
        builder.Services.AddSingleton<UserSession>();
        builder.Services.AddTransient<UserSessionHeaderHandler>();
        builder.Services.AddTransient<ResilienceHandler>();

        var settingsStore = new ClientSettingsStore();
        var clientSettings = settingsStore.LoadSettings();
        var baseAddressStr = clientSettings.ServerBaseAddress;
        if (string.IsNullOrWhiteSpace(baseAddressStr) || baseAddressStr == "http://localhost:5000/")
        {
            baseAddressStr = builder.Configuration["BackendSettings:BaseAddress"] ?? "http://localhost:5000/";
        }
        if (!baseAddressStr.EndsWith("/")) baseAddressStr += "/";
        var baseAddressUri = new Uri(baseAddressStr);

        // 8.9-B6: HealthPollingService SINGLETON. Antes se registraba con AddHttpClient<T,T>
        // (transient): MainViewModel y el job de arranque resolvían instancias distintas y la
        // suscripción a OnHealthRecovered podía perderse. Ahora hay un único HealthPollingService
        // con su propio HttpClient dedicado (sin ResilienceHandler). El timeout del cliente se
        // fija en StartPolling via CancellationToken linkeado (8.9-M12).
        builder.Services.AddHttpClient("HealthPolling", client =>
        {
            client.BaseAddress = baseAddressUri;
        });
        builder.Services.AddSingleton<IHealthPollingService>(sp =>
            new HealthPollingService(
                sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("HealthPolling"),
                sp.GetService<IClientStateService>(),
                sp.GetService<IConnectionManager>()));

        builder.Services.AddHttpClient<IProductService, ProductService>(client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddHttpClient("SalesApi", client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddSingleton<ISalesService>(sp => 
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("SalesApi");
            return new SalesService(httpClient);
        });

        builder.Services.AddHttpClient<IPaymentService, PaymentService>(client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddHttpClient("ExchangeRateApi", client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddSingleton<IExchangeRateService>(sp => 
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ExchangeRateApi");
            return new ExchangeRateService(httpClient, sp.GetRequiredService<IDispatcherInvoker>());
        });

        builder.Services.AddHttpClient<IUserService, UserService>(client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        // 8.9-L13: los VMs retenidos de por vida por MainViewModel son de-facto singletons;
        // se registran Singleton para que el contenedor refleje su ciclo de vida real.
        builder.Services.AddSingleton<LoginViewModel>();
        builder.Services.AddTransient<PairingQrViewModel>();
        builder.Services.AddTransient<ServerConnectionViewModel>();
        builder.Services.AddTransient<CustomerManagementViewModel>();
        builder.Services.AddSingleton<UsersManagementViewModel>();


        builder.Services.AddSingleton<CartViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<PosViewModel>();
        builder.Services.AddSingleton<InventoryViewModel>();
        builder.Services.AddSingleton<SalesHistoryViewModel>();
        builder.Services.AddSingleton<PendingOrdersViewModel>(sp => new Desktop.Client.ViewModels.PendingOrdersViewModel(
            sp.GetRequiredService<Desktop.Client.Services.ISalesService>(),
            sp.GetRequiredService<Desktop.Client.Services.IExchangeRateService>(),
            sp.GetRequiredService<Desktop.Client.Services.IPaymentService>(),
            sp.GetRequiredService<Desktop.Client.Services.IDialogService>(),
            sp.GetRequiredService<Desktop.Client.Services.UserSession>()));


        builder.Services.AddSingleton<PendingPickupsViewModel>();

        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<ExchangeRateViewModel>();

        // Register new Cash Drawer Service
        builder.Services.AddHttpClient<ICashDrawerService, CashDrawerService>(client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddHttpClient<ISettingsService, SettingsService>(client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddHttpClient<IDailyClosureClientService, DailyClosureClientService>(client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddHttpClient<IVersionCheckService, VersionCheckService>(client =>
        {
            client.BaseAddress = baseAddressUri;
            // 8.9-M12: el version-check de arranque nunca debe colgar la UI; timeout duro de 5s.
            client.Timeout = TimeSpan.FromSeconds(5);
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddHttpClient<IProductImportService, ProductImportService>(client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddSingleton<DailyClosureViewModel>();
        builder.Services.AddSingleton<CashDrawerViewModel>();
        builder.Services.AddSingleton<ImportProductsViewModel>();

        // Main Window Registration
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            if (ev.ExceptionObject is Exception ex)
            {
                Core.Logging.AppLogger.LogCrash(ex, "WpfDesktop.UnhandledException");
            }
        };

        DispatcherUnhandledException += (s, ev) =>
        {
            Core.Logging.AppLogger.LogCrash(ev.Exception, "WpfDesktop.DispatcherUnhandledException");
        };

        try
        {
            Core.Logging.AppLogger.LogStart("WPF Desktop Client initializing...");

            _host = CreateAndStartHost(e.Args);
            await _host.StartAsync();

            var versionService = _host.Services.GetRequiredService<IVersionCheckService>();
            // 8.9-M12: timeout de 5s en el arranque — si el servidor no responde, se continúa
            // con la versión actual (el servicio degrada a IsCompatible=true y no bloquea).
            using var versionCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var checkResult = await versionService.CheckVersionAsync(versionCts.Token);

            if (!checkResult.IsCompatible)
            {
                var currentVersion = Core.Common.AppVersionHelper.CurrentVersion;
                Core.Logging.AppLogger.LogStart($"Client version obsolete. Installed: {currentVersion}, Required: {checkResult.MinimumClientVersion}. Displaying lockout modal.");
                var lockoutVm = new ViewModels.VersionLockoutViewModel(
                    currentVersion,
                    checkResult.MinimumClientVersion,
                    checkResult.UpdateServerUrl,
                    _host.Services.GetRequiredService<IAppShutdown>());
                var lockoutDialog = new Views.VersionLockoutDialog(lockoutVm);
                lockoutDialog.ShowDialog();
                ShutdownReason = "Versión del cliente no compatible";
                Shutdown();
                return;
            }

            Core.Logging.AppLogger.LogStart("WPF Desktop Client version check passed. Displaying MainWindow.");

            // Restaurar sesión previa resguardada en DPAPI (H-DCC-11 / R7)
            try
            {
                var userSession = _host.Services.GetRequiredService<UserSession>();
                if (userSession.TryRestoreTokenFromStorage())
                {
                    Core.Logging.AppLogger.LogStart($"Sesión previa restaurada exitosamente para '{userSession.UserName}' ({userSession.CurrentUser?.Role}).");
                }
            }
            catch (Exception ex)
            {
                Core.Logging.AppLogger.LogCrash(ex, "App.OnStartup.RestoreToken");
            }

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception fatalEx)
        {
            Core.Logging.AppLogger.LogCrash(fatalEx, "WpfDesktop.OnStartupFatal");
            throw;
        }
    }

    /// <summary>
    /// Detiene y dispone de manera asíncrona y no bloqueante los servicios en segundo plano y el Generic Host.
    /// </summary>
    public async Task StopServicesAsync()
    {
        if (_isServicesStopped) return;
        _isServicesStopped = true;

        if (_host != null)
        {
            try
            {
                Core.Logging.AppLogger.LogStart("Deteniendo servicio de sondeo de salud...");
                var healthService = _host.Services.GetService<IHealthPollingService>();
                if (healthService != null)
                {
                    healthService.StopPolling();
                    Core.Logging.AppLogger.LogStart("Servicio de sondeo de salud detenido con éxito.");
                }
            }
            catch (Exception ex)
            {
                Core.Logging.AppLogger.LogCrash(ex, "App.StopServicesAsync.StopHealthPolling");
            }

            try
            {
                Core.Logging.AppLogger.LogStart("Disponiendo servicio de tasa de cambio y cerrando SignalR...");
                var exchangeRateService = _host.Services.GetService<IExchangeRateService>();
                if (exchangeRateService is IAsyncDisposable asyncExchange)
                {
                    // 8.6-B5: se espera el teardown asíncrono (no Dispose().Wait) desde hilo no-UI.
                    await asyncExchange.DisposeAsync();
                    Core.Logging.AppLogger.LogStart("Servicio de tasa de cambio dispuesto con éxito.");
                }
                else if (exchangeRateService is IDisposable disposableExchange)
                {
                    disposableExchange.Dispose();
                    Core.Logging.AppLogger.LogStart("Servicio de tasa de cambio dispuesto con éxito.");
                }
            }
            catch (Exception ex)
            {
                Core.Logging.AppLogger.LogCrash(ex, "App.StopServicesAsync.DisposeExchangeRate");
            }

            try
            {
                Core.Logging.AppLogger.LogStart("Deteniendo Generic Host asíncronamente...");
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _host.StopAsync(cts.Token);
                Core.Logging.AppLogger.LogStart("Generic Host detenido con éxito.");
            }
            catch (Exception ex)
            {
                Core.Logging.AppLogger.LogCrash(ex, "App.StopServicesAsync.StopHost");
            }

            try
            {
                _host.Dispose();
                _host = null;
                Core.Logging.AppLogger.LogStart("Generic Host dispuesto con éxito.");
            }
            catch (Exception ex)
            {
                Core.Logging.AppLogger.LogCrash(ex, "App.StopServicesAsync.DisposeHost");
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsShutdownRequested = true;
        if (string.IsNullOrEmpty(ShutdownReason)) ShutdownReason = "Cierre de la ventana principal";
        Core.Logging.AppLogger.LogStart($"WPF Desktop Client shutting down. Motivo: {ShutdownReason}");

        if (!_isServicesStopped && _host != null)
        {
            try
            {
                // 8.6-B5: el apagado canónico es ASÍNCRONO en MainWindow.OnClosing (await StopServicesAsync)
                // y ya está completo antes de OnExit. Esta rama es un respaldo best-effort para rutas
                // alternativas: NO se bloquea la UI con Wait/GetResult; se delega al pool y el proceso
                // sigue su salida (el Host limita su propio shutdown vía HostOptions.ShutdownTimeout).
                Core.Logging.AppLogger.LogStart("OnExit: el apagado asíncrono no se completó (ruta alternativa); disponiendo host best-effort.");
                _ = Task.Run(() => _host.Dispose());
            }
            catch (Exception ex)
            {
                Core.Logging.AppLogger.LogCrash(ex, "App.OnExit.StopServices");
            }
        }

        try
        {
            Program.ReleaseSingleInstanceMutex();
        }
        catch { }

        base.OnExit(e);
    }
}
