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
    private readonly string _crashPath = GetCrashPath();

    private static string GetCrashPath()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "start.bat")) || File.Exists(Path.Combine(dir.FullName, "Start.bat")))
            {
                return Path.Combine(dir.FullName, "crash.txt");
            }
            dir = dir.Parent;
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt");
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
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { File.AppendAllText(_crashPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Task Exception: " + e.Exception.Message + "\n" + e.Exception.StackTrace + "\n\n"); } catch { }
        e.SetObserved();
    }

    public IHost CreateAndStartHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Service Registration
        builder.Services.AddSingleton<IClientStateService, ClientStateService>();
        builder.Services.AddSingleton<IDialogService, WpfDialogService>();
        builder.Services.AddSingleton<IJitterProvider, ProductionJitterProvider>();
        builder.Services.AddSingleton<UserSession>();
        builder.Services.AddTransient<UserSessionHeaderHandler>();
        builder.Services.AddTransient<ResilienceHandler>();

        var baseAddressStr = builder.Configuration["BackendSettings:BaseAddress"] ?? "http://localhost:5000/";
        if (!baseAddressStr.EndsWith("/")) baseAddressStr += "/";
        var baseAddressUri = new Uri(baseAddressStr);

        // Register HealthPollingService with dedicated HttpClient (without ResilienceHandler loop)
        builder.Services.AddHttpClient<IHealthPollingService, HealthPollingService>(client =>
        {
            client.BaseAddress = baseAddressUri;
        });

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
            return new ExchangeRateService(httpClient);
        });

        builder.Services.AddHttpClient<IUserService, UserService>(client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<CustomerManagementViewModel>();
        builder.Services.AddTransient<UsersManagementViewModel>();


        builder.Services.AddSingleton<CartViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<PosViewModel>();
        builder.Services.AddTransient<InventoryViewModel>();
        builder.Services.AddSingleton<SalesHistoryViewModel>();
        builder.Services.AddSingleton<PendingOrdersViewModel>(sp => new Desktop.Client.ViewModels.PendingOrdersViewModel(
            sp.GetRequiredService<Desktop.Client.Services.ISalesService>(),
            sp.GetRequiredService<Desktop.Client.Services.IExchangeRateService>(),
            sp.GetRequiredService<Desktop.Client.Services.IPaymentService>(),
            sp.GetRequiredService<Desktop.Client.Services.IDialogService>(),
            sp.GetRequiredService<Desktop.Client.Services.UserSession>()));


        builder.Services.AddSingleton<PendingPickupsViewModel>();

        builder.Services.AddTransient<SettingsViewModel>();
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
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddHttpClient<IProductImportService, ProductImportService>(client =>
        {
            client.BaseAddress = baseAddressUri;
        }).AddHttpMessageHandler<UserSessionHeaderHandler>().AddHttpMessageHandler<ResilienceHandler>();

        builder.Services.AddTransient<DailyClosureViewModel>();
        builder.Services.AddTransient<CashDrawerViewModel>();
        builder.Services.AddTransient<ImportProductsViewModel>();

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
            var checkResult = await versionService.CheckVersionAsync();

            if (!checkResult.IsCompatible)
            {
                Core.Logging.AppLogger.LogStart($"Client version obsolete. Installed: 1.0.0, Required: {checkResult.MinimumClientVersion}. Displaying lockout modal.");
                var lockoutVm = new ViewModels.VersionLockoutViewModel("1.0.0", checkResult.MinimumClientVersion, checkResult.UpdateServerUrl);
                var lockoutDialog = new Views.VersionLockoutDialog(lockoutVm);
                lockoutDialog.ShowDialog();
                ShutdownReason = "Versión del cliente no compatible";
                Shutdown();
                return;
            }

            Core.Logging.AppLogger.LogStart("WPF Desktop Client version check passed. Displaying MainWindow.");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception fatalEx)
        {
            Core.Logging.AppLogger.LogCrash(fatalEx, "WpfDesktop.OnStartupFatal");
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsShutdownRequested = true;
        if (string.IsNullOrEmpty(ShutdownReason)) ShutdownReason = "Cierre de la ventana principal";
        Core.Logging.AppLogger.LogStart($"WPF Desktop Client shutting down. Motivo: {ShutdownReason}");

        // Detener el sondeo de salud antes de disponer el contenedor DI, para que ningún
        // bucle de polling quede vivo durante el cierre.
        try
        {
            _host?.Services.GetService<IHealthPollingService>()?.StopPolling();
        }
        catch { }

        _host?.Dispose();
        base.OnExit(e);
    }
}
