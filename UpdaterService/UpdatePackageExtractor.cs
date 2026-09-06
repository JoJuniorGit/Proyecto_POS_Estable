using System;
using System.IO;
using System.IO.Compression;
using System.Security;

namespace UpdaterService;

public static class UpdatePackageExtractor
{
    private const int UnixSymlinkMask = 0xF000;
    private const int UnixSymlinkFlag = 0xA000;

    /// <summary>
    /// Extrae un paquete de actualización ZIP dentro de targetDir aplicando controles de seguridad:
    /// - Prevención contra Zip Slip / Path Traversal (rutas relativas ../ y rutas absolutas).
    /// - Detección y rechazo de enlaces simbólicos (Symlinks / Reparse Points).
    /// - Preservación de archivos de configuración solo a nivel de raíz (appsettings.Production.json y appsettings.Development.json).
    /// - Registro y auditoría en consola o delegado de log de cualquier intento sospechoso.
    /// </summary>
    public static void Extract(string packagePath, string targetDir, Action<string>? logAction = null)
    {
        var log = logAction ?? Console.WriteLine;

        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            throw new FileNotFoundException("El archivo de actualización no existe o la ruta está vacía.", packagePath);
        }

        string canonicalTargetDir = Path.GetFullPath(targetDir);
        if (!canonicalTargetDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            canonicalTargetDir += Path.DirectorySeparatorChar;
        }

        log($"[Updater] Iniciando extracción segura de '{packagePath}' en '{canonicalTargetDir}'...");

        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue; // Entrada de directorio

            // 1. Detección y rechazo de rutas absolutas
            if (Path.IsPathRooted(entry.FullName))
            {
                log($"[ZipSlip_AUDIT] Intento de extracción con ruta absoluta detectado: '{entry.FullName}'");
                throw new SecurityException($"[ZipSlip] Ruta absoluta no permitida en archivo ZIP: '{entry.FullName}'");
            }

            // 2. Detección y mitigación de Symlinks / Reparse Points
            int unixMode = entry.ExternalAttributes >> 16;
            bool isUnixSymlink = (unixMode & UnixSymlinkMask) == UnixSymlinkFlag;
            bool isWindowsReparse = (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
            if (isUnixSymlink || isWindowsReparse)
            {
                log($"[ZipSlip_AUDIT] Enlace simbólico o Reparse Point omitido por seguridad: '{entry.FullName}'");
                continue;
            }

            // 3. Validación de escape de directorio (Zip Slip relativo ../)
            string destinationPath = Path.GetFullPath(Path.Combine(canonicalTargetDir, entry.FullName));
            if (!destinationPath.StartsWith(canonicalTargetDir, StringComparison.OrdinalIgnoreCase))
            {
                log($"[ZipSlip_AUDIT] Intento de evasión de directorio detectado: Archivo='{entry.FullName}', Destino Canónico='{canonicalTargetDir}', Calculado='{destinationPath}'");
                throw new SecurityException($"[ZipSlip] Intento de extracción fuera del directorio destino detectado: '{entry.FullName}'");
            }

            // 4. Regla de preservación de configuraciones SOLO en la raíz del paquete
            bool isRootConfigFile = (entry.FullName.Equals("appsettings.Production.json", StringComparison.OrdinalIgnoreCase) ||
                                     entry.FullName.Equals("appsettings.Development.json", StringComparison.OrdinalIgnoreCase));

            if (isRootConfigFile)
            {
                log($"[Updater] Preservando archivo de configuración existente del usuario: {entry.FullName}");
                continue;
            }

            // 5. Creación de subdirectorios de destino si no existen
            string? destDir = Path.GetDirectoryName(destinationPath);
            if (destDir != null && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            try
            {
                entry.ExtractToFile(destinationPath, overwrite: true);
            }
            catch (Exception ex)
            {
                log($"[Updater] Advertencia al extraer '{entry.FullName}': {ex.Message}");
            }
        }

        log("[Updater] Extracción completada exitosamente.");
    }
}
