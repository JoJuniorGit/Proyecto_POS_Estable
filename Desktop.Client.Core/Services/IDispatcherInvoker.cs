using System;

namespace Desktop.Client.Services;

/// <summary>
/// Abstraccion del despachador del hilo de UI (8.20-M01). Sustituye el uso directo de
/// <c>System.Windows.Application.Current.Dispatcher</c> en Desktop.Client.Core para
/// desacoplar la capa de WPF. La implementacion WPF concreta vive en Desktop.Client.
/// </summary>
public interface IDispatcherInvoker
{
    /// <summary>True si la llamada actual ya se ejecuta en el hilo de UI.</summary>
    bool CheckAccess();

    /// <summary>Ejecuta <paramref name="action"/> sincronicamente en el hilo de UI.</summary>
    void Invoke(Action action);

    /// <summary>Programa <paramref name="action"/> de forma asincrona en el hilo de UI.</summary>
    void BeginInvoke(Action action);

    /// <summary>Ejecuta <paramref name="action"/> en el hilo de UI y completa cuando termina.</summary>
    Task InvokeAsync(Action action);
}