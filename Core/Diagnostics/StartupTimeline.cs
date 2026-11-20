using System;
using System.Diagnostics;
using Core.Logging;

namespace Core.Diagnostics;

/// <summary>
/// Marcas monotonicas de arranque/navegacion para medir con Stopwatch. Se habilita solo con
/// POS_STARTUP_TIMING=1; sin la variable no hay salida ni overhead perceptible.
/// </summary>
public static class StartupTimeline
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly bool IsEnabled = IsTruthy(Environment.GetEnvironmentVariable("POS_STARTUP_TIMING"));

    public static bool Enabled => IsEnabled;

    public static long ElapsedMilliseconds => Clock.ElapsedMilliseconds;

    public static void Mark(string name)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        AppLogger.LogStart($"[STARTUP] {name} at {Clock.ElapsedMilliseconds} ms");
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
