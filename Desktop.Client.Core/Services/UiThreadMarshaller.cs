using System;
using System.Windows;

namespace Desktop.Client.Services;

/// <summary>
/// 8.9-M14: marshalling único hacia el hilo de UI. Centraliza el patrón repetido
/// `Application.Current?.Dispatcher` / `CheckAccess` / `Invoke` que aparecía duplicado
/// en múltiples ViewModels, de modo que el comportamiento del marshalling sea idéntico
/// en toda la aplicación.
/// </summary>
public static class UiThreadMarshaller
{
    /// <summary>
    /// Ejecuta <paramref name="action"/> en el hilo de UI. Si ya se está en él (o no hay
    /// dispatcher disponible, p. ej. en pruebas sin Application.Current), se ejecuta inline.
    /// </summary>
    public static void Invoke(Action action)
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
}