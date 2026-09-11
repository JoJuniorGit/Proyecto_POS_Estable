namespace Desktop.Client.Services;

/// <summary>
/// Implementacion por defecto de <see cref="IFilePickerDialog"/> (8.20-M01) que siempre cancela
/// (devuelve null), para ViewModels sin dialogo de archivo en pruebas. En produccion
/// Desktop.Client inyecta <c>WpfFilePickerDialog</c>.
/// </summary>
public sealed class NoopFilePicker : IFilePickerDialog
{
    public string? PickFilePath(string title, string filter) => null;
    public string? PickSaveFilePath(string title, string filter, string defaultFileName) => null;
}