using System;
using System.Threading.Tasks;
using Core.Logging;

namespace Core.Common;

public static class TaskExtensions
{
    /// <summary>
    /// Executes a Task asynchronously without blocking the UI thread,
    /// safely catching and logging any unhandled exceptions to prevent crash loops (HWPF-3 / HDCC-5).
    /// </summary>
    public static async void SafeFireAndForget(this Task task, string context = "FireAndForget", Action<Exception>? onError = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash(ex, $"SafeFireAndForget.{context}");
            onError?.Invoke(ex);
        }
    }
}
