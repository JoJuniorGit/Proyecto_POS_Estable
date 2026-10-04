using System;
using System.Collections.Generic;
using Inventory.Module.Services.Ocr;
using OpenCvSharp;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class OcrImagePreprocessorTests
{
    /// <summary>Tolerancia documentada para el deskew sintético (grados).</summary>
    private const double SkewToleranceDegrees = 1.5;

    private readonly ImagePreprocessor _sut = new();

    private static Mat CreateInkOnPaper(int width, int height)
    {
        var image = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        for (var line = 0; line < 4; line++)
        {
            var top = 40 + (line * 40);
            Cv2.Rectangle(image, new Rect(30, top, width - 60, 14), Scalar.Black, thickness: -1);
        }

        return image;
    }

    private static Mat RotateImage(Mat source, double angleDegrees)
    {
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

    private static IEnumerable<byte> EnumerateGray(Mat image)
    {
        var height = image.Height;
        var width = image.Width;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                yield return image.At<byte>(y, x);
            }
        }
    }

    [Fact]
    public void ToGrayscale_ColorImage_ReturnsSingleChannelWithSameSize()
    {
        using var source = CreateInkOnPaper(320, 240);
        using var gray = _sut.ToGrayscale(source);

        Assert.Equal(1, gray.Channels());
        Assert.Equal(source.Width, gray.Width);
        Assert.Equal(source.Height, gray.Height);
    }

    [Fact]
    public void Binarize_HighContrastImage_UsesOtsuAndProducesOnlyBlackAndWhite()
    {
        using var gray = _sut.ToGrayscale(CreateInkOnPaper(320, 240));
        using var binary = _sut.Binarize(gray, out var usedAdaptiveThreshold);

        Assert.False(usedAdaptiveThreshold);
        var values = EnumerateGray(binary);
        Assert.All(values, value => Assert.True(value == 0 || value == 255, $"valor no binario: {value}"));
        Assert.Contains(values, value => value == 0);
        Assert.Contains(values, value => value == 255);
    }

    [Fact]
    public void Binarize_LowVarianceImage_UsesAdaptiveFallbackAndProducesBinaryValues()
    {
        using var flat = new Mat(80, 200, MatType.CV_8UC1, new Scalar(128));
        flat.At<byte>(40, 50) = 120;

        using var binary = _sut.Binarize(flat, out var usedAdaptiveThreshold);

        Assert.True(usedAdaptiveThreshold, "una imagen de baja varianza debe activar el fallback adaptativo");
        Assert.All(EnumerateGray(binary), value => Assert.True(value == 0 || value == 255, $"valor no binario: {value}"));
    }

    [Fact]
    public void EstimateSkewAngle_RotatedSyntheticText_DetectsAppliedRotation()
    {
        using var source = CreateInkOnPaper(600, 300);
        using var rotated = RotateImage(source, 7.0);
        using var gray = _sut.ToGrayscale(rotated);
        using var binary = _sut.Binarize(gray);

        var angle = _sut.EstimateSkewAngle(binary);

        Assert.InRange(Math.Abs(angle), 4.0, 10.0);
    }

    [Fact]
    public void Deskew_RotatedSyntheticText_LeavesResidualSkewWithinTolerance()
    {
        using var source = CreateInkOnPaper(600, 300);
        using var rotated = RotateImage(source, 7.0);
        using var gray = _sut.ToGrayscale(rotated);
        using var binary = _sut.Binarize(gray);

        using var deskewed = _sut.Deskew(binary);
        var residual = _sut.EstimateSkewAngle(deskewed);

        Assert.True(
            Math.Abs(residual) <= SkewToleranceDegrees,
            $"skew residual {residual}° supera la tolerancia de {SkewToleranceDegrees}°");
    }

    [Fact]
    public void Deskew_ClockwiseRotatedSyntheticText_ReportsAppliedRotationAndStraightens()
    {
        using var source = CreateInkOnPaper(600, 300);
        using var rotated = RotateImage(source, -6.0);
        using var gray = _sut.ToGrayscale(rotated);
        using var binary = _sut.Binarize(gray);

        var estimated = _sut.EstimateSkewAngle(binary);
        Assert.InRange(estimated, -8.0, -4.0);

        using var deskewed = _sut.Deskew(binary);
        var residual = _sut.EstimateSkewAngle(deskewed);
        Assert.True(
            Math.Abs(residual) <= SkewToleranceDegrees,
            $"skew residual {residual}° supera la tolerancia de {SkewToleranceDegrees}°");
    }

    [Fact]
    public void EstimateSkewAngle_BlankImage_ReturnsZero()
    {
        using var blank = new Mat(120, 200, MatType.CV_8UC1, Scalar.White);

        Assert.Equal(0.0, _sut.EstimateSkewAngle(blank));
    }

    [Fact]
    public void Preprocess_ColorInput_KeepsPixelDimensionsAndProducesBinaryOutput()
    {
        using var source = CreateInkOnPaper(400, 250);

        using var result = _sut.Preprocess(source, out var detectedSkewAngle);

        Assert.Equal(source.Width, result.Width);
        Assert.Equal(source.Height, result.Height);
        Assert.Equal(1, result.Channels());
        Assert.InRange(Math.Abs(detectedSkewAngle), 0.0, 1.0);
        Assert.All(EnumerateGray(result), value => Assert.True(value == 0 || value == 255, $"valor no binario: {value}"));
    }

    [Fact]
    public void EncodePng_DecodePng_RoundTripsPixelsAndSize()
    {
        using var original = new Mat(2, 3, MatType.CV_8UC3, new Scalar(10, 20, 30));
        original.At<Vec3b>(0, 0) = new Vec3b(1, 2, 3);
        original.At<Vec3b>(1, 2) = new Vec3b(200, 150, 100);

        var encoded = _sut.EncodePng(original);
        using var decoded = _sut.DecodePng(encoded);

        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.Height, decoded.Height);
        Assert.Equal(original.Channels(), decoded.Channels());

        var height = original.Height;
        var width = original.Width;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var expected = original.At<Vec3b>(y, x);
                var actual = decoded.At<Vec3b>(y, x);
                Assert.Equal(expected.Item0, actual.Item0);
                Assert.Equal(expected.Item1, actual.Item1);
                Assert.Equal(expected.Item2, actual.Item2);
            }
        }
    }

    [Fact]
    public void EncodePng_DecodePng_SingleChannel_RoundTripsAsSingleChannel()
    {
        using var original = new Mat(2, 2, MatType.CV_8UC1, new Scalar(128));

        using var decoded = _sut.DecodePng(_sut.EncodePng(original));

        Assert.Equal(1, decoded.Channels());
        Assert.Equal(128, decoded.At<byte>(0, 0));
    }

    [Fact]
    public void DecodePng_InvalidBytes_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _sut.DecodePng(new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<ArgumentException>(() => _sut.DecodePng(Array.Empty<byte>()));
    }
}
