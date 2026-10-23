using System;
using System.IO;
using System.Threading.Tasks;

namespace Core.Logging;

public static class AppLogger
{
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
    public static string WarnLogPath => Path.Combine(_logsDir, "warn.log");

    /// <summary>
    /// Aguarda a que todas las entradas encoladas hasta este punto estén escritas en disco.
    /// </summary>
    public static Task FlushAsync() => AsyncLogSink.FlushAsync();

    public static void LogStart(string message)
    {
        WriteLog(StartLogPath, "START", message);
    }

    public static void LogWarn(string message, string context = "General")
    {
        WriteLog(WarnLogPath, "WARN", $"Context: {context}\nMessage: {message}");
    }

    public static void LogSecurityAudit(string message)
    {
        WriteLog(SecurityAuditLogPath, "SECURITY-AUDIT", message);
    }

    /// <summary>
    /// SRE-01: Registro estructurado en formato JSON para ingestión directa por Grafana Loki, Datadog o Elasticsearch.
    /// </summary>
    public static void LogStructuredSecurity(string action, int? userId, string path, string? ipAddress, string reason, object? extraContext = null)
    {
        var entry = new
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            Level = "SECURITY-AUDIT",
            Action = action,
            UserId = userId,
            Path = path,
            IpAddress = ipAddress,
            Reason = reason,
            Context = extraContext
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        AsyncLogSink.Enqueue(SecurityAuditLogPath, json + Environment.NewLine);
    }

    /// <summary>
    /// SRE-01: Registro estructurado de eventos operativos con contexto clave-valor.
    /// </summary>
    public static void LogStructured(string filePath, string level, string message, object? context = null)
    {
        var entry = new
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            Level = level,
            Message = message,
            Context = context
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        AsyncLogSink.Enqueue(filePath, json + Environment.NewLine);
    }

    public static void LogCrash(Exception ex, string context = "General")
    {
#if DEBUG
        var msg = $"Context: {context}\nException: {ex.GetType().FullName}\nMessage: {ex.Message}\nStackTrace:\n{ex.StackTrace}";
        if (ex.InnerException != null)
        {
            msg += $"\nInner Exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
        }
#else
        var msg = $"Context: {context}\nException: {ex.GetType().FullName}\nMessage: {ex.Message}";
        if (ex.InnerException != null)
        {
            msg += $"\nInner Exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}";
        }
#endif
        WriteLog(CrashLogPath, "CRASH", msg);
    }

    public static void LogCrash(string message, string context = "General")
    {
        WriteLog(CrashLogPath, "CRASH", $"Context: {context}\nMessage: {message}");
    }

    public static void LogDbError(Exception ex, string context = "Database")
    {
#if DEBUG
        var msg = $"Context: {context}\nException: {ex.GetType().FullName}\nMessage: {ex.Message}\nStackTrace:\n{ex.StackTrace}";
        if (ex.InnerException != null)
        {
            msg += $"\nInner Exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
        }
#else
        var msg = $"Context: {context}\nException: {ex.GetType().FullName}\nMessage: {ex.Message}";
        if (ex.InnerException != null)
        {
            msg += $"\nInner Exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}";
        }
#endif
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
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var formatted = $"[{timestamp}] [{level}] {message}\n----------------------------------------\n";
        AsyncLogSink.Enqueue(filePath, formatted);
    }
}
