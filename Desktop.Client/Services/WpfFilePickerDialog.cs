using System.Windows;
using Desktop.Client.Services;
using Microsoft.Win32;

namespace Desktop.Client.Services;

/// <summary>
/// Implementacion WPF de <see cref="IFilePickerDialog"/> (8.20-M01): selector de archivo via
/// <c>Microsoft.Win32.OpenFileDialog</c>. Inyectada por Desktop.Client en produccion.
/// </summary>
public sealed class WpfFilePickerDialog : IFilePickerDialog
{
    public string? PickFilePath(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSaveFilePath(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}