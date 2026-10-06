using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace CommandCenter.Wpf.E2ETests.Fixtures;

public class WpfAppFixture : IDisposable
{
    // Cada test que llama Launch() espera una app fresca. La instancia anterior se termina en
    // Launch() y todas las restantes en Dispose(); sin esto quedaban procesos Desktop.Client.exe
    // huérfanos que heredan el stdout del testhost y hacen que un `dotnet test ... | Select-Object`
    // parezca colgado aunque las pruebas ya hayan terminado.
    private readonly List<Application> _launchedApps = new();
    private bool _disposed;

    public Application? App => _launchedApps.Count > 0 ? _launchedApps[^1] : null;
    public UIA3Automation Automation { get; }
    public Window? MainWindow { get; private set; }

    public WpfAppFixture()
    {
        Automation = new UIA3Automation();
    }

    /// <summary>
    /// Lanza Desktop.Client.exe. Por defecto conserva el modo mock (<c>--e2e</c>); el harness
    /// full-stack pasa <paramref name="arguments"/> vacío y variables de entorno por proceso
    /// (p. ej. <c>BackendSettings__BaseAddress</c>) sin tocar el comportamiento existente.
    /// </summary>
    public Window Launch(
        string? appPath = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string arguments = "--e2e")
    {
        TerminateLaunchedApps();

        if (appPath == null)
        {
            // Search standard build output directories
            var baseDir = AppContext.BaseDirectory;
            var candidate1 = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\Desktop.Client\bin\Release\net10.0-windows\Desktop.Client.exe"));
            var candidate2 = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\Desktop.Client\bin\Debug\net10.0-windows\Desktop.Client.exe"));
            var candidate3 = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\Desktop.Client\bin\Release\net10.0-windows10.0.19041.0\Desktop.Client.exe"));
            var candidate4 = Path.GetFullPath(Path.Combine(baseDir, @"Desktop.Client.exe"));

            appPath = File.Exists(candidate1) ? candidate1 :
                      File.Exists(candidate2) ? candidate2 :
                      File.Exists(candidate3) ? candidate3 :
                      candidate4;
        }

        if (!File.Exists(appPath))
        {
            throw new FileNotFoundException($"Desktop.Client.exe not found at '{appPath}'. Ensure the WPF project is built before running E2E tests.");
        }

        var processStartInfo = new ProcessStartInfo(appPath)
        {
            WorkingDirectory = Path.GetDirectoryName(appPath),
            Arguments = arguments
        };

        if (environment != null)
        {
            // ProcessStartInfo.Environment hereda el entorno actual y aplica overrides por proceso.
            foreach (var variable in environment)
            {
                processStartInfo.Environment[variable.Key] = variable.Value;
            }
        }

        var app = Application.Launch(processStartInfo);
        _launchedApps.Add(app);
        MainWindow = app.GetMainWindow(Automation, TimeSpan.FromSeconds(15));
        return MainWindow;
    }

    private void TerminateLaunchedApps()
    {
        for (int i = _launchedApps.Count - 1; i >= 0; i--)
        {
            Terminate(_launchedApps[i]);
        }

        _launchedApps.Clear();
        MainWindow = null;
    }

    private static void Terminate(Application? app)
    {
        if (app == null) return;

        try
        {
            if (!app.HasExited)
            {
                // Close(killIfCloseFails: true) cierra con gracia y fuerza el kill si el cierre
                // no completa en CloseTimeout (la ventana puede interceptar WM_CLOSE).
                app.Close(killIfCloseFails: true);
            }

            if (!app.HasExited)
            {
                app.Kill();
            }
        }
        catch
        {
            // Ignored on cleanup
        }
        finally
        {
            try
            {
                app.Dispose();
            }
            catch
            {
                // Ignored on cleanup
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TerminateLaunchedApps();
        Automation.Dispose();
        System.Threading.Thread.Sleep(300);
    }
}
