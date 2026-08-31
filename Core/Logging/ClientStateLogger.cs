using System;
using System.IO;

namespace Core.Logging;

public static class ClientStateLogger
{
    private static readonly object _lock = new object();
    private static readonly string _logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

    static ClientStateLogger()
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

    public static string ResilienceLogPath => Path.Combine(_logsDir, "client-resilience.log");

    public static void LogRetry(int attempt, int maxAttempts, string requestUri, string method)
    {
        WriteLog("WARNING", "ResilienceHandler", $"[HTTP 503] Reintentando petición {method} {requestUri} tras fallo transitorio (Intento {attempt} de {maxAttempts})...");
    }

    public static void LogRetriesExhausted(string requestUri, string method)
    {
        WriteLog("WARNING", "ResilienceHandler", $"[HTTP 503] Reintentos agotados para {method} {requestUri}. Transicionando a HealthPollingService en segundo plano.");
    }

    public static void LogHealthRecovery()
    {
        WriteLog("INFO", "HealthPolling", "[HTTP 200] Conectividad restablecida con /health. Deteniendo sondeo.");
    }

    public static void LogAuditSuppressedReplay(string operationName)
    {
        WriteLog("WARNING", "SalesService", $"[AUDIT] Operación transaccional interrumpida (\"^{operationName}\"). Auto-replay suprimido; control e idempotencia delegados al cajero.");
    }

    public static void LogFatalDbAuth()
    {
        WriteLog("FATAL", "ResilienceHandler", "[DB_AUTH_FAILED] Fallo de credenciales en PostgreSQL detectado. Abortando auto-recuperación.");
    }

    public static void LogInfo(string message, string origin = "SYSTEM")
    {
        WriteLog("INFO", origin, message);
    }

    public static void LogWarning(string message, string origin = "SYSTEM")
    {
        WriteLog("WARNING", origin, message);
    }

    public static void LogError(string message, string origin = "SYSTEM")
    {
        WriteLog("ERROR", origin, message);
    }

    private static void WriteLog(string level, string origin, string message)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(ResilienceLogPath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // ISO 8601 Timestamp format
                var isoTimestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var formatted = $"[{isoTimestamp}] [{level}] [{origin}] {message}\n";
                File.AppendAllText(ResilienceLogPath, formatted);
            }
            catch { }
        }
    }
}
