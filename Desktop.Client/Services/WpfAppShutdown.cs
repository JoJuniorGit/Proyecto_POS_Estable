using System.Windows;
using Desktop.Client.Services;

namespace Desktop.Client.Services;

/// <summary>
/// Implementacion WPF de <see cref="IAppShutdown"/> (8.20-M01): cierre de la aplicacion
/// via <c>Application.Current.Shutdown()</c>. Inyectada por Desktop.Client en produccion.
/// </summary>
public sealed class WpfAppShutdown : IAppShutdown
{
    public void Shutdown()
    {
        Application.Current?.Shutdown();
    }
}