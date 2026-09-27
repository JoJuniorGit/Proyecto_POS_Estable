using System;

namespace Desktop.Client.Services;

/// <summary>
/// Implementacion por defecto (8.20-M01) que ejecuta de forma inline/sincrona, sin requerir
/// WPF ni un hilo de UI. Se usa como valor por defecto de los ViewModels en pruebas y como
/// fallback cuando no hay despachador; en produccion Desktop.Client inyecta la implementacion
/// WPF real (<c>WpfDispatcherInvoker</c>).
/// </summary>
public sealed class InlineDispatcherInvoker : IDispatcherInvoker
{
    public bool CheckAccess() => true;

    public void Invoke(Action action) => action?.Invoke();

    public void BeginInvoke(Action action) => action?.Invoke();

    public Task InvokeAsync(Action action)
    {
        action?.Invoke();
        return Task.CompletedTask;
    }
}