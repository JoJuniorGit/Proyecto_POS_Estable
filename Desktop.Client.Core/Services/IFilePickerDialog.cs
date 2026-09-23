namespace Desktop.Client.Services;

/// <summary>
/// Abstraccion del selector de archivo (8.20-M01). Sustituye el uso directo de
/// <c>Microsoft.Win32.OpenFileDialog</c> en Desktop.Client.Core para desacoplar la capa de WPF.
/// </summary>
public interface IFilePickerDialog
{
    /// <summary>Muestra el dialogo de seleccion y devuelve la ruta elegida, o null si se cancela.</summary>
    string? PickFilePath(string title, string filter);

    /// <summary>Muestra el dialogo de guardado (SaveFileDialog) y devuelve la ruta elegida, o null si se cancela.</summary>
    string? PickSaveFilePath(string title, string filter, string defaultFileName);
}