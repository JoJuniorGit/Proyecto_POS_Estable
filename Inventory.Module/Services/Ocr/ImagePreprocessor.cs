using Core.DTOs;
using OpenCvSharp;

namespace Inventory.Module.Services.Ocr;

/// <summary>
/// 8.147-T2/D3: preprocesamiento OCR como pasos puros sobre <see cref="Mat"/> (sin WPF).
/// Orden del pipeline: escala de grises → contraste (CLAHE) → binarización (Otsu con fallback
/// adaptativo) → deskew (ángulo por líneas Hough de la tinta, fallback minAreaRect; rotación
/// con relleno blanco). Cada paso devuelve un Mat nuevo y no dispone ni muta la entrada.
/// </summary>
public sealed class ImagePreprocessor
{
    /// <summary>Desviación estándar de grises bajo la cual Otsu se considera poco fiable.</summary>
    public const double LowVarianceStdDevThreshold = 18.0;

    /// <summary>8.170: tamaño de bloque del umbral adaptativo de respaldo (validado con fotos reales).</summary>
    public const int FallbackAdaptiveBlockSize = 51;

    /// <summary>8.170: constante C del umbral adaptativo de respaldo (validada con fotos reales).</summary>
    public const double FallbackAdaptiveConstant = 15.0;

    /// <summary>Ángulo (grados) bajo el cual el deskew no rota (evita remuestreos inútiles).</summary>
    public const double MinDeskewAngleDegrees = 0.1;

    /// <summary>Inclinación máxima aceptada como línea de texto al estimar el ángulo.</summary>
    private const double MaxTextLineAngleDegrees = 30.0;

    /// <summary>Mínimo de píxeles de tinta para intentar el fallback por <c>minAreaRect</c>.</summary>
    private const double MinimumInkPixels = 50.0;

    /// <summary>Convierte a gris (1 canal). Si la entrada ya es de 1 canal, devuelve un clon.</summary>
    public Mat ToGrayscale(Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Channels() switch
        {
            1 => source.Clone(),
            3 => source.CvtColor(ColorConversionCodes.BGR2GRAY),
            4 => source.CvtColor(ColorConversionCodes.BGRA2GRAY),
            var channels => throw new ArgumentException(
                $"La imagen debe tener 1, 3 o 4 canales; se recibieron {channels}.", nameof(source))
        };
    }

    /// <summary>Aplica CLAHE sobre una imagen de 1 canal.</summary>
    public Mat IncreaseContrast(Mat grayscale)
    {
        ArgumentNullException.ThrowIfNull(grayscale);
        using var gray = ToGrayscale(grayscale);
        using var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
        var contrasted = new Mat();
        clahe.Apply(gray, contrasted);
        return contrasted;
    }

    /// <summary>Binariza (Otsu; fallback adaptativo si la varianza es baja) manteniendo tinta oscura sobre fondo claro.</summary>
    public Mat Binarize(Mat grayscale) => Binarize(grayscale, out _);

    /// <summary>Binariza e informa si se usó el fallback adaptativo.</summary>
    public Mat Binarize(Mat grayscale, out bool usedAdaptiveThreshold)
    {
        ArgumentNullException.ThrowIfNull(grayscale);
        using var gray = ToGrayscale(grayscale);
        Cv2.MeanStdDev(gray, out _, out var deviation);

        var minDimension = Math.Min(gray.Width, gray.Height);
        usedAdaptiveThreshold = deviation.Val0 < LowVarianceStdDevThreshold && minDimension >= 3;

        var binarized = new Mat();
        if (usedAdaptiveThreshold)
        {
            var blockSize = ComputeAdaptiveBlockSize(minDimension);
            Cv2.AdaptiveThreshold(
                gray, binarized, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, blockSize, 10);
        }
        else
        {
            Cv2.Threshold(gray, binarized, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        }

        return binarized;
    }

    /// <summary>
    /// Estima el ángulo de inclinación (grados; 0 = sin inclinación) a partir de la tinta de una
    /// imagen binarizada con tinta oscura sobre fondo claro. Positivo = contenido rotado en
    /// sentido antihorario (misma convención que <see cref="Cv2.GetRotationMatrix2D"/>).
    /// </summary>
    public double EstimateSkewAngle(Mat binarized)
    {
        ArgumentNullException.ThrowIfNull(binarized);
        using var gray = ToGrayscale(binarized);
        using var ink = new Mat();
        Cv2.BitwiseNot(gray, ink);

        // Máscara binaria explícita: el deskew puede re-estimarse sobre una imagen ya remuestreada
        // con grises por la rotación.
        using var binaryInk = new Mat();
        Cv2.Threshold(ink, binaryInk, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        var fromLines = EstimateAngleFromHoughLines(binaryInk);
        if (fromLines.HasValue)
        {
            return fromLines.Value;
        }

        return EstimateAngleFromInkBounds(binaryInk);
    }

    /// <summary>Rota la imagen alrededor de su centro con relleno blanco.</summary>
    public Mat Rotate(Mat source, double angleDegrees)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Math.Abs(angleDegrees) < 0.0001)
        {
            return source.Clone();
        }

        var center = new Point2f(source.Width / 2f, source.Height / 2f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angleDegrees, 1.0);
        var rotated = new Mat();
        Cv2.WarpAffine(
            source,
            rotated,
            matrix,
            new Size(source.Width, source.Height),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.White);
        return rotated;
    }

    /// <summary>Endereza una imagen binarizada con tinta oscura sobre fondo claro.</summary>
    public Mat Deskew(Mat binarized)
    {
        ArgumentNullException.ThrowIfNull(binarized);
        var angle = EstimateSkewAngle(binarized);
        if (Math.Abs(angle) < MinDeskewAngleDegrees)
        {
            return binarized.Clone();
        }

        return Rotate(binarized, -angle);
    }

    /// <summary>Pipeline completo de preprocesamiento.</summary>
    public Mat Preprocess(Mat source) => Preprocess(source, out _);

    /// <summary>Pipeline completo; informa el ángulo de inclinación detectado.</summary>
    public Mat Preprocess(Mat source, out double detectedSkewAngleDegrees)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var gray = ToGrayscale(source);
        using var contrasted = IncreaseContrast(gray);
        using var binarized = Binarize(contrasted);

        detectedSkewAngleDegrees = EstimateSkewAngle(binarized);
        if (Math.Abs(detectedSkewAngleDegrees) < MinDeskewAngleDegrees)
        {
            return binarized.Clone();
        }

        return Rotate(binarized, -detectedSkewAngleDegrees);
    }

    /// <summary>
    /// Preprocesa una página aplicando los parámetros visuales de la plantilla OCR del proveedor
    /// (rotación manual, recorte ROI normalizado, contraste/brillo y umbral de binarización).
    /// </summary>
    public Mat PreprocessWithTemplate(Mat source, SupplierOcrTemplateDto template)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(template);

        var hasManualRotation = double.IsFinite(template.RotationDegrees) && Math.Abs(template.RotationDegrees) >= 0.01;
        using var rotated = hasManualRotation ? Rotate(source, template.RotationDegrees) : null;
        var afterRotation = rotated ?? source;

        using var cropped = TryCropRoi(afterRotation, template);
        var afterCrop = cropped ?? afterRotation;

        using var gray = ToGrayscale(afterCrop);
        var contrast = double.IsFinite(template.ContrastFactor) && template.ContrastFactor > 0.01
            ? Math.Clamp(template.ContrastFactor, 0.1, 5.0)
            : 1.0;
        var brightness = double.IsFinite(template.BrightnessDelta)
            ? Math.Clamp(template.BrightnessDelta, -255.0, 255.0)
            : 0.0;

        using var adjusted = new Mat();
        gray.ConvertTo(adjusted, MatType.CV_8UC1, contrast, brightness);

        if (template.BinarizationThreshold is >= 1 and <= 254)
        {
            var fixedBinarized = new Mat();
            Cv2.Threshold(adjusted, fixedBinarized, template.BinarizationThreshold, 255, ThresholdTypes.Binary);
            return fixedBinarized;
        }

        using var contrasted = IncreaseContrast(adjusted);
        var binarized = Binarize(contrasted);
        if (hasManualRotation)
        {
            return binarized;
        }

        using (binarized)
        {
            return Deskew(binarized);
        }
    }

    private static Mat? TryCropRoi(Mat source, SupplierOcrTemplateDto template)
    {
        if (source.Width <= 1 || source.Height <= 1)
        {
            return null;
        }

        if (template.RoiWidth is not double rawW ||
            template.RoiHeight is not double rawH ||
            !double.IsFinite(rawW) ||
            !double.IsFinite(rawH) ||
            rawW is <= 0.01 or > 1.0 ||
            rawH is <= 0.01 or > 1.0)
        {
            return null;
        }

        var normX = template.RoiX is double rawX && double.IsFinite(rawX) ? Math.Clamp(rawX, 0.0, 0.99) : 0.0;
        var normY = template.RoiY is double rawY && double.IsFinite(rawY) ? Math.Clamp(rawY, 0.0, 0.99) : 0.0;
        var normW = Math.Clamp(rawW, 0.01, 1.0 - normX);
        var normH = Math.Clamp(rawH, 0.01, 1.0 - normY);

        if (normX <= 0.001 && normY <= 0.001 && normW >= 0.999 && normH >= 0.999)
        {
            return null;
        }

        var x = Math.Clamp((int)Math.Round(normX * source.Width), 0, source.Width - 1);
        var y = Math.Clamp((int)Math.Round(normY * source.Height), 0, source.Height - 1);
        var width = Math.Clamp((int)Math.Round(normW * source.Width), 1, source.Width - x);
        var height = Math.Clamp((int)Math.Round(normH * source.Height), 1, source.Height - y);

        using var subMat = new Mat(source, new Rect(x, y, width, height));
        return subMat.Clone();
    }

    /// <summary>
    /// 8.170: candidato de respaldo para imágenes de bajo contraste donde el umbral automático
    /// pierde la tabla (validado contra fotos reales: 4D mejorada y previas). Fuerza el umbral
    /// adaptativo (bloque 51 / C 15) y NO aplica deskew: la estimación de inclinación sobre estas
    /// imágenes resultó destructiva en los casos verificados (rotación espuria). Se usa solo
    /// cuando el pipeline principal no produce filas usables.
    /// </summary>
    public Mat PreprocessAdaptiveFallback(Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var gray = ToGrayscale(source);
        using var contrasted = IncreaseContrast(gray);
        var binarized = new Mat();
        Cv2.AdaptiveThreshold(
            contrasted,
            binarized,
            255,
            AdaptiveThresholdTypes.GaussianC,
            ThresholdTypes.Binary,
            FallbackAdaptiveBlockSize,
            FallbackAdaptiveConstant);
        return binarized;
    }

    /// <summary>Codifica un Mat a PNG (previews y entrada del motor OCR).</summary>
    public byte[] EncodePng(Mat image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!Cv2.ImEncode(".png", image, out var encoded))
        {
            throw new ArgumentException("No se pudo codificar la imagen a PNG.", nameof(image));
        }

        return encoded;
    }

    /// <summary>Decodifica bytes PNG a un Mat conservando canales.</summary>
    public Mat DecodePng(byte[] encodedBytes)
    {
        ArgumentNullException.ThrowIfNull(encodedBytes);
        var decoded = Cv2.ImDecode(encodedBytes, ImreadModes.Unchanged);
        if (decoded.Empty())
        {
            decoded.Dispose();
            throw new ArgumentException("Los bytes no corresponden a un PNG válido.", nameof(encodedBytes));
        }

        return decoded;
    }

    private static double? EstimateAngleFromHoughLines(Mat binaryInk)
    {
        var minLineLength = Math.Max(30.0, Math.Min(binaryInk.Width, binaryInk.Height) / 4.0);
        var lines = Cv2.HoughLinesP(binaryInk, 1.0, Math.PI / 180.0, 60, minLineLength, 12);
        var tilts = new List<double>(lines.Length);
        foreach (var line in lines)
        {
            var deltaX = line.P2.X - line.P1.X;
            if (Math.Abs(deltaX) < 2)
            {
                continue; // bordes verticales (tablas) no describen la línea de texto
            }

            var tilt = ToTiltAngle(Math.Atan2(line.P2.Y - line.P1.Y, deltaX) * 180.0 / Math.PI);
            if (Math.Abs(tilt) <= MaxTextLineAngleDegrees)
            {
                tilts.Add(tilt);
            }
        }

        if (tilts.Count < 2)
        {
            return null;
        }

        tilts.Sort();
        return tilts[tilts.Count / 2];
    }

    private static double EstimateAngleFromInkBounds(Mat binaryInk)
    {
        using var nonZero = new Mat();
        Cv2.FindNonZero(binaryInk, nonZero);
        if (nonZero.Empty() || nonZero.Total() < MinimumInkPixels)
        {
            return 0.0;
        }

        // La arista más larga del rectángulo mínimo de la tinta marca la dirección del texto.
        var corners = Cv2.MinAreaRect(nonZero).Points();
        var bestLengthSquared = -1.0;
        var bestScreenAngle = 0.0;
        for (var i = 0; i < corners.Length; i++)
        {
            var start = corners[i];
            var end = corners[(i + 1) % corners.Length];
            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var lengthSquared = (deltaX * deltaX) + (deltaY * deltaY);
            if (lengthSquared > bestLengthSquared)
            {
                bestLengthSquared = lengthSquared;
                bestScreenAngle = Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI;
            }
        }

        var normalized = NormalizeToHalfQuadrant(bestScreenAngle);
        if (Math.Abs(normalized) > MaxTextLineAngleDegrees)
        {
            return 0.0;
        }

        return -normalized;
    }

    private static double ToTiltAngle(double screenAngleDegrees) => -NormalizeToHalfQuadrant(screenAngleDegrees);

    private static double NormalizeToHalfQuadrant(double degrees)
    {
        var normalized = ((degrees + 90.0) % 180.0 + 180.0) % 180.0 - 90.0;
        if (normalized > 45.0)
        {
            normalized -= 90.0;
        }
        else if (normalized < -45.0)
        {
            normalized += 90.0;
        }

        return normalized;
    }

    private static int ComputeAdaptiveBlockSize(int minDimension)
    {
        var blockSize = Math.Min(31, minDimension);
        if (blockSize % 2 == 0)
        {
            blockSize--;
        }

        return Math.Max(3, blockSize);
    }
}
