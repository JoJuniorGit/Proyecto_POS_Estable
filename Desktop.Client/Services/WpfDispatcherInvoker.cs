using System;
using System.Threading.Tasks;
using System.Windows;
using Desktop.Client.Services;

namespace Desktop.Client.Services;

/// <summary>
/// Implementacion WPF de <see cref="IDispatcherInvoker"/> (8.20-M01): marshalling al hilo de UI
/// via <c>Application.Current.Dispatcher</c>. Inyectada por Desktop.Client en produccion.
/// </summary>
public sealed class WpfDispatcherInvoker : IDispatcherInvoker
{
    public bool CheckAccess()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher == null || dispatcher.CheckAccess();
    }

    public void Invoke(Action action)
    {
        if (action == null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    public void BeginInvoke(Action action)
    {
        if (action == null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public Task InvokeAsync(Action action)
    {
        if (action == null) return Task.CompletedTask;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }
}