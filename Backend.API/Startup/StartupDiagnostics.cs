namespace Backend.API.Startup;

/// <summary>
/// Expone el resultado del bootstrap de base de datos para observabilidad (endpoint de health).
/// El estado es estático por proceso: lo escribe el arranque y lo lee health.
/// </summary>
public static class StartupDiagnostics
{
    private static readonly object _lock = new();
    private static string _convergenceStatus = "not-run";
    private static string? _convergenceVersion;
    private static string? _convergenceError;

    /// <param name="status">"not-run" | "skipped" | "ok" | "failed".</param>
    public static void RecordConvergence(string status, string? version, string? error)
    {
        lock (_lock)
        {
            _convergenceStatus = status;
            _convergenceVersion = version;
            _convergenceError = error;
        }
    }

    public static (string Status, string? Version, string? Error) GetConvergence()
    {
        lock (_lock)
        {
            return (_convergenceStatus, _convergenceVersion, _convergenceError);
        }
    }
}
