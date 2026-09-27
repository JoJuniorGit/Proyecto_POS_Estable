using System;
using System.Diagnostics;
using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace CommandCenter.Wpf.E2ETests.Fixtures;

public class WpfAppFixture : IDisposable
{
    public Application? App { get; private set; }
    public UIA3Automation Automation { get; }
    public Window? MainWindow { get; private set; }

    public WpfAppFixture()
    {
        Automation = new UIA3Automation();
    }

    public Window Launch(string? appPath = null)
    {
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
            Arguments = "--e2e"
        };

        App = Application.Launch(processStartInfo);
        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(15));
        return MainWindow;
    }

    public void Dispose()
    {
        try
        {
            if (App != null && !App.HasExited)
            {
                App.Close();
                if (!App.HasExited)
                {
                    App.Kill();
                }
            }
        }
        catch
        {
            // Ignored on cleanup
        }
        finally
        {
            App?.Dispose();
            Automation.Dispose();
            System.Threading.Thread.Sleep(300);
        }
    }
}
