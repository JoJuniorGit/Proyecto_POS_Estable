using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace CommandCenter.Wpf.E2ETests.Fixtures;

/// <summary>
/// Reintentos acotados EXCLUSIVAMENTE para fallas transitorias de transporte de UI Automation
/// (<see cref="COMException"/>, por ejemplo el timeout 0x80131505 que aparece bajo carga).
/// </summary>
/// <remarks>
/// La política es de transporte, nunca de aserción: <b>NUNCA</b> se debe envolver una aserción con
/// este helper ni usarlo para re-evaluar un resultado lógico. Un valor devuelto por la operación se
/// retorna tal cual (el fallo lo decide la aserción del test); cualquier excepción que no sea
/// <see cref="COMException"/> se propaga de inmediato sin reintentar; al agotar el presupuesto de
/// intentos se propaga la ÚLTIMA falla COM observada, sin enmascararla. No usar este helper para
/// reintentar un test completo.
/// </remarks>
public static class UiaRetry
{
    /// <summary>Intentos máximos por defecto para una operación UIA (D7: ≤4).</summary>
    public const int DefaultAttempts = 4;

    /// <summary>Backoff por defecto entre intentos (D7: ~500 ms).</summary>
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>Variable de entorno que ajusta el timeout de búsqueda de los helpers de flujo.</summary>
    public const string FindTimeoutEnvironmentVariable = "E2E_FIND_TIMEOUT_SECONDS";

    /// <summary>Timeout de búsqueda por defecto cuando la variable no existe o es inválida (10 s).</summary>
    public static readonly TimeSpan DefaultFindTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Ejecuta <paramref name="operation"/> reintentando solo ante <see cref="COMException"/> de UI Automation.
    /// </summary>
    public static T? RetryUia<T>(Func<T?> operation, int attempts = DefaultAttempts, TimeSpan? backoff = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        var delay = backoff ?? DefaultBackoff;
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(backoff), backoff, "El backoff no puede ser negativo.");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (COMException) when (attempt < attempts)
            {
                // Falla transitoria de transporte: se agota el presupuesto y, en el último intento,
                // la excepción se propaga SIN capturarla (preserva la falla y su stack original).
                Sleep(delay);
            }
        }
    }

    /// <summary>
    /// Sobrecarga void de <see cref="RetryUia{T}"/> para operaciones sin valor de retorno
    /// (por ejemplo <c>Retry.WhileNotNull(...)</c>).
    /// </summary>
    public static void RetryUia(Action operation, int attempts = DefaultAttempts, TimeSpan? backoff = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RetryUia<object?>(
            () =>
            {
                operation();
                return null;
            },
            attempts,
            backoff);
    }

    /// <summary>
    /// Timeout de búsqueda efectivo para los helpers de flujo, configurable por
    /// <see cref="FindTimeoutEnvironmentVariable"/>.
    /// </summary>
    public static TimeSpan FindTimeout =>
        ParseFindTimeout(Environment.GetEnvironmentVariable(FindTimeoutEnvironmentVariable));

    /// <summary>
    /// Parsea el timeout de búsqueda (pura y testeable). Un valor ausente, no numérico, no finito,
    /// negativo o cero cae al <see cref="DefaultFindTimeout"/> documentado.
    /// </summary>
    public static TimeSpan ParseFindTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultFindTimeout;
        }

        // InvariantCulture: el valor de entorno es un número decimal de máquina ("15", "12.5"),
        // independiente de la cultura del equipo.
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) ||
            seconds <= 0)
        {
            return DefaultFindTimeout;
        }

        try
        {
            return TimeSpan.FromSeconds(seconds);
        }
        catch (OverflowException)
        {
            return DefaultFindTimeout;
        }
    }

    private static void Sleep(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }
}
