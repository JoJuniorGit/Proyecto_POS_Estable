using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Desktop.Client.Converters;

/// <summary>
/// 8.147-S5 (T7): convierte la preview PNG base64 que devuelve el backend en un
/// <see cref="BitmapImage"/> para el panel de revisión lado a lado. Entrada vacía o inválida
/// devuelve null (la celda queda sin imagen, sin excepción).
/// </summary>
public class Base64PngToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string base64 || string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var bytes = System.Convert.FromBase64String(base64);
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
