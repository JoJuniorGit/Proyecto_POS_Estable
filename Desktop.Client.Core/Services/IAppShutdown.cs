namespace Desktop.Client.Services;

/// <summary>
/// Abstraccion del cierre de la aplicacion (8.20-M01). Sustituye el uso directo de
/// <c>System.Windows.Application.Current.Shutdown()</c> en Desktop.Client.Core.
/// Se nombra <c>IAppShutdown</c> para no colisionar con
/// <c>Microsoft.Extensions.Hosting.IApplicationLifetime</c>.
/// </summary>
public interface IAppShutdown
{
    /// <summary>Solicita el cierre de la aplicacion.</summary>
    void Shutdown();
}