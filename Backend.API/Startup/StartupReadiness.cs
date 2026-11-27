using System;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Startup;

/// <summary>
/// Gate de disponibilidad del arranque. En modo diferido Kestrel empieza a escuchar antes de
/// que la base esté migrada; el middleware responde 503 y los jobs esperan antes de tocar DB.
/// Por defecto está listo para no alterar Development/tests ni el arranque clásico.
/// </summary>
public static class StartupReadiness
{
    private static TaskCompletionSource _ready = CreateReady();

    public static bool IsReady => _ready.Task.IsCompletedSuccessfully;

    public static void MarkNotReady()
        => _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void MarkReady()
        => _ready.TrySetResult();

    public static void MarkFailed(Exception exception)
        => _ready.TrySetException(exception);

    public static async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        if (IsReady)
        {
            return;
        }

        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TaskCompletionSource CreateReady()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
