using Core.Interfaces;
using Tesseract;

namespace Inventory.Module.Services.Ocr;

/// <summary>
/// 8.147-T2/D1/D14: motor Tesseract 5 (spa+eng) detrás de <see cref="IOcrEngine"/>.
/// El datapath por defecto es <c>AppContext.BaseDirectory/tessdata</c> (tessdata vendido en
/// Backend.API/tessdata y copiado al output); se puede sobreescribir por constructor o por la
/// variable de entorno <c>TESSDATA_PREFIX</c>. La creación del engine es diferida: construir el
/// servicio no toca nativos y el primer reconocimiento falla con un mensaje claro si falta
/// tessdata. Tesseract no es thread-safe, por eso los reconocimientos se serializan.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine, IDisposable
{
    public const string DefaultLanguages = "spa+eng";
    public const string DataPathEnvironmentVariable = "TESSDATA_PREFIX";

    private readonly string _dataPath;
    private readonly string _languages;
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly Lazy<TesseractEngine> _engine;
    private bool _disposed;

    public TesseractOcrEngine()
        : this(null)
    {
    }

    /// <param name="dataPath">Directorio tessdata; null resuelve TESSDATA_PREFIX o el base dir.</param>
    /// <param name="languages">Idiomas separados por '+'; null usa <see cref="DefaultLanguages"/>.</param>
    public TesseractOcrEngine(string? dataPath, string? languages = null)
    {
        _dataPath = ResolveDataPath(dataPath);
        _languages = string.IsNullOrWhiteSpace(languages) ? DefaultLanguages : languages;
        _engine = new Lazy<TesseractEngine>(CreateEngine, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Directorio tessdata efectivo (útil para diagnóstico/tests).</summary>
    public string DataPath => _dataPath;

    public async Task<OcrPage> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() => Recognize(image), cancellationToken).ConfigureAwait(false);
    }

    private OcrPage Recognize(OcrImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _processGate.Wait();
        try
        {
            using var pix = Pix.LoadFromMemory(image.EncodedBytes);
            using var page = _engine.Value.Process(pix);
            using var iterator = page.GetIterator();
            var words = new List<OcrWord>();

            iterator.Begin();
            do
            {
                if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
                {
                    continue;
                }

                var text = iterator.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var confidence = Math.Clamp(iterator.GetConfidence(PageIteratorLevel.Word), 0f, 100f);
                words.Add(new OcrWord(
                    text.Trim(),
                    bounds.X1,
                    bounds.Y1,
                    bounds.Width,
                    bounds.Height,
                    confidence));
            }
            while (iterator.Next(PageIteratorLevel.Word));

            return new OcrPage(words);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private TesseractEngine CreateEngine()
    {
        if (!Directory.Exists(_dataPath))
        {
            throw new InvalidOperationException(
                $"No se encontró el directorio tessdata en '{_dataPath}'. Verifique el despliegue de " +
                "Backend.API/tessdata o configure TESSDATA_PREFIX.");
        }

        return new TesseractEngine(_dataPath, _languages, EngineMode.Default);
    }

    private static string ResolveDataPath(string? explicitDataPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataPath))
        {
            return Path.GetFullPath(explicitDataPath);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return Path.Combine(AppContext.BaseDirectory, "tessdata");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_engine.IsValueCreated)
        {
            _engine.Value.Dispose();
        }

        _processGate.Dispose();
    }
}
