using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Core.Logging;

/// <summary>
/// Canal acotado compartido por los loggers estáticos: los productores solo encolan
/// (nunca bloquean ni tocan disco) y un único consumidor LongRunning serializa la E/S
/// en orden FIFO preservando el contrato de archivos y rotación existente.
/// </summary>
internal static class AsyncLogSink
{
    private const int ChannelCapacity = 4096;
    private const int FlushTimeoutMs = 5000;
    private const int ProcessExitFlushTimeoutMs = 2000;
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxArchiveFiles = 5;

    private static readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private static readonly ChannelReader<LogEntry> _reader = _channel.Reader;
    private static readonly ChannelWriter<LogEntry> _writer = _channel.Writer;

    private static long _dropped;

    static AsyncLogSink()
    {
        // Consumidor dedicado fuera del ThreadPool para que la E/S no compita con el camino de request.
        _ = Task.Factory.StartNew(
            ConsumeLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Drenaje best-effort: evita perder crash.log en muertes gestionadas del proceso.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                FlushAsync().Wait(ProcessExitFlushTimeoutMs);
            }
            catch { }
        };
    }

    public static void Enqueue(string filePath, string line)
    {
        if (!_writer.TryWrite(new LogEntry(filePath, line, null)))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public static async Task FlushAsync()
    {
        if (_reader.Completion.IsCompleted)
        {
            return;
        }

        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // A diferencia de Enqueue, el llamador de flush sí puede ceder a la contrapresión del canal.
            await _writer.WriteAsync(new LogEntry(string.Empty, string.Empty, marker)).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return;
        }

        await Task.WhenAny(marker.Task, Task.Delay(FlushTimeoutMs)).ConfigureAwait(false);
    }

    private static void ConsumeLoop()
    {
        try
        {
            // Hilo dedicado LongRunning: el bloqueo síncrono es deliberado (no hay contexto async que capturar).
            while (_reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_reader.TryRead(out var entry))
                {
                    ProcessEntry(entry);
                }
            }
        }
        catch { }
    }

    private static void ProcessEntry(LogEntry entry)
    {
        try
        {
            if (entry.FlushMarker is not null)
            {
                entry.FlushMarker.TrySetResult();
                return;
            }

            EnsureDirectory(entry.FilePath);
            RotateLogFileIfNeeded(entry.FilePath);
            WriteDroppedEntriesNotice(entry.FilePath);
            File.AppendAllText(entry.FilePath, entry.Line);
        }
        catch
        {
            // Una entrada fallida (disco/permisos) se descarta en silencio; el consumidor continúa.
        }
    }

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void WriteDroppedEntriesNotice(string filePath)
    {
        if (Volatile.Read(ref _dropped) <= 0)
        {
            return;
        }

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            File.AppendAllText(filePath, $"[LOGGER] {dropped} entradas descartadas por saturación del canal de logging\n");
        }
    }

    private static void RotateLogFileIfNeeded(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists || fileInfo.Length < MaxFileSizeBytes)
            {
                return;
            }

            string oldestArchive = $"{filePath}.{MaxArchiveFiles}";
            if (File.Exists(oldestArchive))
            {
                File.Delete(oldestArchive);
            }

            for (int i = MaxArchiveFiles - 1; i >= 1; i--)
            {
                string source = $"{filePath}.{i}";
                string destination = $"{filePath}.{i + 1}";
                if (File.Exists(source))
                {
                    File.Move(source, destination, true);
                }
            }

            File.Move(filePath, $"{filePath}.1", true);
        }
        catch { }
    }

    private sealed record LogEntry(string FilePath, string Line, TaskCompletionSource? FlushMarker);
}
