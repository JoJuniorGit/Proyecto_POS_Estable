using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Core.Logging;

/// <summary>
/// Canal acotado compartido por los loggers estáticos: los productores solo encolan
/// (nunca bloquean ni tocan disco) y un único consumidor LongRunning serializa la E/S
/// en orden FIFO preservando el contrato de archivos y rotación existente.
///
/// 8.203: la escritura usa un <see cref="StreamWriter"/> bufferizado por archivo en lugar de
/// abrir/cerrar el archivo en cada línea; los fallos de E/S se contabilizan y se reportan a
/// stderr, nunca se silencian con un catch vacío.
/// </summary>
internal static class AsyncLogSink
{
    private const int ChannelCapacity = 4096;
    private const int FlushTimeoutMs = 5000;
    private const int ProcessExitFlushTimeoutMs = 2000;
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxArchiveFiles = 5;
    private const int FlushEveryEntries = 256;

    private static readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private static readonly ChannelReader<LogEntry> _reader = _channel.Reader;
    private static readonly ChannelWriter<LogEntry> _writer = _channel.Writer;
    private static readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);

    private static long _dropped;
    private static long _writeFailures;
    private static int _entriesSinceFlush;
    private static volatile string? _lastError;

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
                DisposeWriters();
            }
            catch (Exception ex)
            {
                ReportFailure("ProcessExit", ex);
            }
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

                FlushAllWriters();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("ConsumeLoop", ex);
        }
    }

    private static void ProcessEntry(LogEntry entry)
    {
        if (entry.FlushMarker is not null)
        {
            // Un flush explícito es un punto de sincronización: además de vaciar el buffer se
            // cierran los handles para que lectores externos (tests, tail, soporte) puedan abrir
            // el archivo sin conflicto de sharing en Windows.
            FlushAllWriters();
            DisposeWriters();
            entry.FlushMarker.TrySetResult();
            return;
        }

        try
        {
            EnsureDirectory(entry.FilePath);
            WriteDroppedEntriesNotice(entry.FilePath);
            RotateLogFileIfNeeded(entry.FilePath, entry.Line.Length);

            var writer = GetWriter(entry.FilePath);
            writer.Write(entry.Line);

            if (++_entriesSinceFlush >= FlushEveryEntries)
            {
                FlushAllWriters();
            }
        }
        catch (Exception ex)
        {
            ReportFailure($"Write:{entry.FilePath}", ex);
            CloseWriter(entry.FilePath);
        }
    }

    private static StreamWriter GetWriter(string filePath)
    {
        if (_writers.TryGetValue(filePath, out var existing))
        {
            return existing;
        }

        var stream = new FileStream(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };

        _writers[filePath] = writer;
        return writer;
    }

    private static void FlushAllWriters()
    {
        foreach (var writer in _writers.Values)
        {
            try
            {
                writer.Flush();
            }
            catch (Exception ex)
            {
                ReportFailure("Flush", ex);
            }
        }

        _entriesSinceFlush = 0;
    }

    private static void CloseWriter(string filePath)
    {
        if (!_writers.Remove(filePath, out var writer))
        {
            return;
        }

        try
        {
            writer.Dispose();
        }
        catch (Exception ex)
        {
            ReportFailure($"Close:{filePath}", ex);
        }
    }

    private static void DisposeWriters()
    {
        foreach (var filePath in new List<string>(_writers.Keys))
        {
            CloseWriter(filePath);
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
            GetWriter(filePath).Write(
                $"[LOGGER] {dropped} entradas descartadas por saturación del canal de logging{Environment.NewLine}");
        }
    }

    private static void RotateLogFileIfNeeded(string filePath, int incomingLineLength)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists || fileInfo.Length + incomingLineLength < MaxFileSizeBytes)
            {
                return;
            }

            // El writer debe soltar el handle antes de mover el archivo en Windows.
            CloseWriter(filePath);

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
        catch (Exception ex)
        {
            ReportFailure($"Rotate:{filePath}", ex);
        }
    }

    private static void ReportFailure(string context, Exception exception)
    {
        Interlocked.Increment(ref _writeFailures);
        _lastError = $"{context}: {exception.GetType().Name}: {exception.Message}";

        try
        {
            Console.Error.WriteLine($"[LOGGER] {_lastError}");
        }
        catch (Exception consoleException)
        {
            // Último recurso sin recursión: se conserva el error en memoria para diagnóstico.
            _lastError = $"{_lastError} | stderr: {consoleException.Message}";
        }
    }

    private sealed record LogEntry(string FilePath, string Line, TaskCompletionSource? FlushMarker);
}
