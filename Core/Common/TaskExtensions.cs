using System;
using System.Threading.Tasks;
using Core.Logging;

namespace Core.Common;

public static class TaskExtensions
{
    /// <summary>
    /// Executes a Task asynchronously without blocking the UI thread,
    /// safely catching and logging any unhandled exceptions to prevent crash loops (HWPF-3 / HDCC-5).
    /// 8.7-M12: patrón sancionado para handlers de eventos/XAML (async void); TODA excepción termina
    /// en AppLogger y en un callback opcional de la VM para reflejar estado (evita discards desnudos).
    /// </summary>
    public static async void SafeFireAndForget(this Task task, string context = "FireAndForget", Action<Exception>? onError = null)
    {
        if (task == null)
        {
            AppLogger.LogCrash(new InvalidOperationException("SafeFireAndForget recibió una tarea null (probable lógica booleana de disparo)."), $"SafeFireAndForget.{context}.NullTask");
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash(ex, $"SafeFireAndForget.{context}");
            try
            {
                onError?.Invoke(ex);
            }
            catch (Exception onErrorEx)
            {
                AppLogger.LogCrash(onErrorEx, $"SafeFireAndForget.{context}.OnErrorCallback");
            }
        }
    }

    /// <summary>
    /// Variante observable (Task) de fire-and-forget: mismas garantías de captura/log, pero devuelve
    /// la tarea para que el llamador pueda observarla en pruebas o continuaciones. Preferir esta forma
    /// para flujos iniciados fuera de handlers de eventos.
    /// </summary>
    public static async Task SafeFireAndForgetAsync(this Task task, string context = "FireAndForget", Action<Exception>? onError = null)
    {
        if (task == null)
        {
            AppLogger.LogCrash(new InvalidOperationException("SafeFireAndForgetAsync recibió una tarea null."), $"SafeFireAndForgetAsync.{context}.NullTask");
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash(ex, $"SafeFireAndForgetAsync.{context}");
            onError?.Invoke(ex);
        }
    }
}
