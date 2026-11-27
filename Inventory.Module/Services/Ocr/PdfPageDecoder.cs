using System;
using System.Buffers.Binary;
using Core.Interfaces;
using OpenCvSharp;
using PDFtoImage;
using PDFtoImage.Exceptions;

namespace Inventory.Module.Services.Ocr;

/// <summary>
/// 8.147-T2/D4: rasteriza un PDF a páginas PNG con PDFtoImage (PDFium). Tope duro de 5 páginas
/// (S4/S7): superarlo es <see cref="ArgumentException"/> y el llamador lo traduce a ProblemDetails.
/// </summary>
public sealed class PdfPageDecoder
{
    /// <summary>Tope de páginas de PDF aceptadas por el flujo OCR.</summary>
    public const int MaxPages = 5;

    /// <summary>DPI de rasterizado usado por defecto (calidad OCR sin archivos gigantes).</summary>
    public const int DefaultDpi = 200;

    public PdfPageDecoder() : this(DefaultDpi)
    {
    }

    public PdfPageDecoder(int dpi)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 1);
        Dpi = dpi;
    }

    public int Dpi { get; }

    /// <summary>Rasteriza todas las páginas del PDF; rechaza más de <see cref="MaxPages"/>.</summary>
    public IReadOnlyList<OcrImage> Decode(byte[] fileBytes)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        if (fileBytes.Length == 0)
        {
            throw new ArgumentException("El archivo PDF está vacío.", nameof(fileBytes));
        }

        // 8.147-T2/D5/L2: el stack nativo OCR del producto se despliega solo en Windows (CI
        // windows-2025, sin nativos Linux). PDFtoImage anota sus APIs con el conjunto completo de
        // plataformas que soporta; se silencia CA1416 localmente para no marcar Inventory.Module
        // (net10.0) como Windows-only ni forzar guardas de plataforma en los llamadores.
#pragma warning disable CA1416
        int pageCount;
        try
        {
            pageCount = Conversion.GetPageCount(fileBytes);
        }
        catch (PdfException exception)
        {
            throw new ArgumentException("Los bytes no corresponden a un PDF válido.", nameof(fileBytes), exception);
        }

        if (pageCount > MaxPages)
        {
            throw new ArgumentException(
                $"El PDF tiene {pageCount} páginas; el máximo permitido es {MaxPages}.", nameof(fileBytes));
        }

        var options = new RenderOptions(Dpi: Dpi);
        var pages = new List<OcrImage>(pageCount);
        for (var page = 0; page < pageCount; page++)
        {
            using var buffer = new MemoryStream();
            Conversion.SavePng(buffer, fileBytes, page, options: options);
            var png = buffer.ToArray();

            // PERF-R04: las dimensiones viven en la cabecera IHDR del PNG; no hace falta
            // decodificar el bitmap completo solo para leerlas.
            var (width, height) = ReadPngDimensions(png, fileBytes);
            pages.Add(new OcrImage(png, width, height));
        }

        return pages;
#pragma warning restore CA1416
    }

    private static (int Width, int Height) ReadPngDimensions(byte[] png, byte[] source)
    {
        if (png.Length < 24
            || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
        {
            throw new ArgumentException("No se pudo rasterizar una página del PDF.", nameof(source));
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("No se pudo rasterizar una página del PDF.", nameof(source));
        }

        return (width, height);
    }
}
