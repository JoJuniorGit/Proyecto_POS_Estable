using System;
using System.Reflection;

namespace Core.Common;

/// <summary>
/// Provee acceso unificado a la versión de la aplicación derivada de los metadatos del ensamblado centralizados en Directory.Build.props.
/// </summary>
public static class AppVersionHelper
{
    private static readonly Lazy<string> _version = new(() =>
    {
        try
        {
            var assembly = typeof(AppVersionHelper).Assembly;
            var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVersion))
            {
                var plusIndex = infoVersion.IndexOf('+');
                return plusIndex > 0 ? infoVersion.Substring(0, plusIndex) : infoVersion;
            }

            var ver = assembly.GetName().Version;
            if (ver != null)
            {
                return $"{ver.Major}.{ver.Minor}.{ver.Build}";
            }
        }
        catch { }

        return "1.0.0";
    });

    public static string CurrentVersion => _version.Value;
}
