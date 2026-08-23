using System;
using System.Threading;
using System.Windows;
using Core.Logging;

namespace Desktop.Client;

public class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, "Global\\POS_Desktop_Client_SingleInstance_Mutex", out createdNew);

        if (!createdNew)
        {
            AppLogger.LogStart("Desktop.Client instance is already running. Aborting secondary launch.");
            return;
        }

        try
        {
            AppLogger.LogStart("Desktop.Client Program.Main starting...");
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (OutOfMemoryException)
        {
            try
            {
                AppLogger.LogCrash("CRITICAL: OutOfMemoryException during startup.", "Program.Main");
            }
            catch { }
            MessageBox.Show("The application ran out of memory during startup.", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash(ex, "Program.Main.StartupError");
            MessageBox.Show("Critical Startup Error: " + ex.Message + "\nSee logs/crash.log for details.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ReleaseSingleInstanceMutex();
            Environment.Exit(0);
        }
    }

    public static void ReleaseSingleInstanceMutex()
    {
        if (_singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
            catch { }
        }
    }
}
