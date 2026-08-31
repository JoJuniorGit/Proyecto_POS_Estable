using System;
using System.IO;

namespace Core.Logging;

public static class AppLogger
{
    private static readonly object _lock = new object();
    private static readonly string _logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

    static AppLogger()
    {
        try
        {
            if (!Directory.Exists(_logsDir))
            {
                Directory.CreateDirectory(_logsDir);
            }
        }
        catch { }
    }

    public static string StartLogPath => Path.Combine(_logsDir, "start.log");
    public static string CrashLogPath => Path.Combine(_logsDir, "crash.log");
    public static string DbErrorsLogPath => Path.Combine(_logsDir, "db-errors.log");
    public static string SecurityAuditLogPath => Path.Combine(_logsDir, "security-audit.log");

    public static void LogStart(string message)
    {
        WriteLog(StartLogPath, "START", message);
    }

    public static void LogSecurityAudit(string message)
    {
        WriteLog(SecurityAuditLogPath, "SECURITY-AUDIT", message);
    }

    public static void LogCrash(Exception ex, string context = "General")
    {
        var msg = $"Context: {context}\nException: {ex.GetType().FullName}\nMessage: {ex.Message}\nStackTrace:\n{ex.StackTrace}";
        if (ex.InnerException != null)
        {
            msg += $"\nInner Exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
        }
        WriteLog(CrashLogPath, "CRASH", msg);
    }

    public static void LogCrash(string message, string context = "General")
    {
        WriteLog(CrashLogPath, "CRASH", $"Context: {context}\nMessage: {message}");
    }

    public static void LogDbError(Exception ex, string context = "Database")
    {
        var msg = $"Context: {context}\nException: {ex.GetType().FullName}\nMessage: {ex.Message}\nStackTrace:\n{ex.StackTrace}";
        if (ex.InnerException != null)
        {
            msg += $"\nInner Exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
        }
        WriteLog(DbErrorsLogPath, "DB-ERROR", msg);
        WriteLog(CrashLogPath, "CRASH", msg);
    }

    public static void LogDbError(string message, string context = "Database")
    {
        var msg = $"Context: {context}\nMessage: {message}";
        WriteLog(DbErrorsLogPath, "DB-ERROR", msg);
        WriteLog(CrashLogPath, "CRASH", msg);
    }

    private static void WriteLog(string filePath, string level, string message)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var formatted = $"[{timestamp}] [{level}] {message}\n----------------------------------------\n";
                File.AppendAllText(filePath, formatted);
            }
            catch { }
        }
    }
}
