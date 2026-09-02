using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Core.Helpers;

namespace Desktop.Client.Helpers;

/// <summary>
/// Listener global no intrusivo para captura de pistolas de código de barras físicas (USB / Bluetooth HID).
/// Discrimina entre ráfagas ultrarrápidas de escáner (intervalos entre caracteres <= 60ms) y tipeo humano,
/// permitiendo el escaneo directo sin requerir que el cajero haga clic previamente en la caja de búsqueda.
/// </summary>
public sealed class KeyboardWedgeScannerListener : IDisposable
{
    private readonly StringBuilder _buffer = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly Func<string, Task> _onBarcodeScanned;
    private readonly int _maxInterKeyIntervalMs;
    private UIElement? _root;
    private bool _disposed;

    /// <param name="onBarcodeScanned">Callback invocado al confirmar una lectura válida de código de barras.</param>
    /// <param name="maxInterKeyIntervalMs">Tiempo máximo en milisegundos entre caracteres consecutivos (por defecto 60 ms).</param>
    public KeyboardWedgeScannerListener(Func<string, Task> onBarcodeScanned, int maxInterKeyIntervalMs = 60)
    {
        _onBarcodeScanned = onBarcodeScanned ?? throw new ArgumentNullException(nameof(onBarcodeScanned));
        _maxInterKeyIntervalMs = Math.Clamp(maxInterKeyIntervalMs, 20, 150);
    }

    /// <summary>
    /// Acopla el listener a un elemento raíz (Vista o Ventana WPF).
    /// </summary>
    public void Attach(UIElement root)
    {
        if (_root != null)
        {
            Detach();
        }

        _root = root ?? throw new ArgumentNullException(nameof(root));
        _root.PreviewTextInput += OnPreviewTextInput;
        _root.PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// Desacopla los manejadores de eventos.
    /// </summary>
    public void Detach()
    {
        if (_root != null)
        {
            _root.PreviewTextInput -= OnPreviewTextInput;
            _root.PreviewKeyDown -= OnPreviewKeyDown;
            _root = null;
        }
        ResetBuffer();
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        // Guarda de foco: si el cajero está editando manualmente un campo de texto interactivo
        // (nombre de cliente, notas, etc.) que NO es el buscador principal, no interceptamos.
        if (IsFocusedOnRestrictedTextInput())
        {
            ResetBuffer();
            return;
        }

        long elapsedMs = _stopwatch.ElapsedMilliseconds;
        _stopwatch.Restart();

        // Si pasó demasiado tiempo desde la última tecla (> umbral), es un nuevo intento/tipeo
        if (_buffer.Length > 0 && elapsedMs > _maxInterKeyIntervalMs)
        {
            _buffer.Clear();
        }

        _buffer.Append(e.Text);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ResetBuffer();
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_buffer.Length >= 4)
            {
                var candidate = _buffer.ToString().Trim();
                if (BarcodeValidator.IsValidBarcode(candidate))
                {
                    e.Handled = true;
                    ResetBuffer();
                    _ = _onBarcodeScanned(candidate);
                    return;
                }
            }

            ResetBuffer();
        }
    }

    private static bool IsFocusedOnRestrictedTextInput()
    {
        var focused = Keyboard.FocusedElement;
        if (focused is TextBox textBox)
        {
            // El buscador principal del POS está permitido para recibir la ráfaga
            if (textBox.Name == "SearchInput" || textBox.Tag?.ToString() == "PosSearchBox")
            {
                return false;
            }

            // Cualquier otro TextBox (ej. notas, cliente, precio personalizado) se protege
            return true;
        }

        if (focused is TextBoxBase && focused is not TextBox)
        {
            return true;
        }

        return false;
    }

    private void ResetBuffer()
    {
        _buffer.Clear();
        _stopwatch.Reset();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }
}
