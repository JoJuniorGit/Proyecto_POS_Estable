using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security;

namespace UpdaterService;

public static class UpdatePackageExtractor
{
    private const int UnixSymlinkMask = 0xF000;
    private const int UnixSymlinkFlag = 0xA000;

    /// <summary>
    /// Extrae un paquete de actualización ZIP aplicando controles de seguridad:
    /// - Extracción a staging con verificación previa al reemplazo en el destino.
    /// - Reemplazo con respaldo del contenido previo y restauración ante fallo.
    /// - Prevención contra Zip Slip / Path Traversal (rutas relativas ../ y rutas absolutas).
    /// - Detección y rechazo de enlaces simbólicos (Symlinks / Reparse Points) y nombres ADS (':').
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

        string stagingDir = Path.Combine(Path.GetTempPath(), "PosUpdaterStaging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        try
        {
            var stagedFiles = ExtractToStaging(packagePath, stagingDir, canonicalTargetDir, log);
            VerifyStaging(stagingDir, stagedFiles, log);
            CommitStaging(stagingDir, canonicalTargetDir, log);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }

        log("[Updater] Extracción completada exitosamente.");
    }

    private static List<(string RelativePath, long Length)> ExtractToStaging(string packagePath, string stagingDir, string canonicalTargetDir, Action<string> log)
    {
        var stagedFiles = new List<(string RelativePath, long Length)>();

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

            if (entry.FullName.Contains(':'))
            {
                log($"[ZipSlip_AUDIT] Nombre de entrada con ADS NTFS ':' detectado: '{entry.FullName}'");
                throw new SecurityException($"[ZipSlip] Nombre de entrada no permitido (ADS NTFS): '{entry.FullName}'");
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

            EnsureNoReparsePointComponents(canonicalTargetDir, destinationPath, entry.FullName, log);

            // 5. Extracción al staging (mismo layout relativo que el destino)
            string relativePath = Path.GetRelativePath(canonicalTargetDir, destinationPath);
            string stagedPath = Path.Combine(stagingDir, relativePath);
            string? stagedDirectory = Path.GetDirectoryName(stagedPath);
            if (stagedDirectory != null && !Directory.Exists(stagedDirectory))
            {
                Directory.CreateDirectory(stagedDirectory);
            }

            entry.ExtractToFile(stagedPath, overwrite: true);
            stagedFiles.Add((relativePath, entry.Length));
        }

        return stagedFiles;
    }

    private static void VerifyStaging(string stagingDir, List<(string RelativePath, long Length)> stagedFiles, Action<string> log)
    {
        foreach (var (relativePath, expectedLength) in stagedFiles)
        {
            var stagedFile = new FileInfo(Path.Combine(stagingDir, relativePath));
            if (!stagedFile.Exists || stagedFile.Length != expectedLength)
            {
                throw new IOException($"[Updater] Verificación de staging fallida para '{relativePath}'.");
            }
        }

        log($"[Updater] Staging verificado ({stagedFiles.Count} archivos).");
    }

    private static void CommitStaging(string stagingDir, string canonicalTargetDir, Action<string> log)
    {
        var stagedFiles = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories);
        string backupDir = Path.Combine(Path.GetTempPath(), "PosUpdaterBackup_" + Guid.NewGuid().ToString("N"));
        var backups = new List<(string Destination, string Backup)>();
        var createdFiles = new List<string>();
        var createdDirectories = new List<string>();
        bool keepBackup = false;

        Directory.CreateDirectory(backupDir);
        try
        {
            foreach (var stagedFile in stagedFiles)
            {
                string relativePath = Path.GetRelativePath(stagingDir, stagedFile);
                string destinationPath = Path.GetFullPath(Path.Combine(canonicalTargetDir, relativePath));
                if (!destinationPath.StartsWith(canonicalTargetDir, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SecurityException($"[ZipSlip] Intento de extracción fuera del directorio destino detectado: '{relativePath}'");
                }

                EnsureNoReparsePointComponents(canonicalTargetDir, destinationPath, relativePath, log);

                string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (destinationDirectory != null && !Directory.Exists(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                    createdDirectories.Add(destinationDirectory);
                }

                if (File.Exists(destinationPath))
                {
                    string backupPath = Path.Combine(backupDir, relativePath);
                    string? backupDirectory = Path.GetDirectoryName(backupPath);
                    if (backupDirectory != null && !Directory.Exists(backupDirectory))
                    {
                        Directory.CreateDirectory(backupDirectory);
                    }

                    File.Copy(destinationPath, backupPath, overwrite: true);
                    backups.Add((destinationPath, backupPath));
                }
                else
                {
                    createdFiles.Add(destinationPath);
                }

                File.Copy(stagedFile, destinationPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            log($"[Updater] ERROR CRÍTICO durante el reemplazo de binarios: {ex.Message}. Restaurando respaldo...");
            bool restored = RollbackCommit(backups, createdFiles, createdDirectories, log);
            if (!restored)
            {
                keepBackup = true;
                log($"[Updater] [AVISO] Restauración incompleta. Respaldo conservado en: '{backupDir}'");
            }
            throw;
        }
        finally
        {
            if (!keepBackup) TryDeleteDirectory(backupDir);
        }

        log("[Updater] Binarios reemplazados con respaldo verificado del contenido previo.");
    }

    private static bool RollbackCommit(List<(string Destination, string Backup)> backups, List<string> createdFiles, List<string> createdDirectories, Action<string> log)
    {
        bool restored = true;

        foreach (var (destination, backup) in backups)
        {
            try
            {
                File.Copy(backup, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                restored = false;
                log($"[Updater] ERROR al restaurar '{destination}' desde '{backup}': {ex.Message}");
            }
        }

        foreach (var createdFile in createdFiles)
        {
            try
            {
                if (File.Exists(createdFile)) File.Delete(createdFile);
            }
            catch (Exception ex)
            {
                restored = false;
                log($"[Updater] ERROR al eliminar archivo nuevo '{createdFile}': {ex.Message}");
            }
        }

        foreach (var createdDirectory in createdDirectories.OrderByDescending(dir => dir.Length))
        {
            try
            {
                if (Directory.Exists(createdDirectory) && !Directory.EnumerateFileSystemEntries(createdDirectory).Any())
                {
                    Directory.Delete(createdDirectory);
                }
            }
            catch (Exception ex)
            {
                restored = false;
                log($"[Updater] ERROR al eliminar directorio nuevo '{createdDirectory}': {ex.Message}");
            }
        }

        return restored;
    }

    private static void EnsureNoReparsePointComponents(string canonicalTargetDir, string destinationPath, string entryName, Action<string> log)
    {
        string relativePath = Path.GetRelativePath(canonicalTargetDir, destinationPath);
        var segments = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        string currentPath = canonicalTargetDir;
        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(currentPath);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex)
            {
                log($"[ZipSlip_AUDIT] No se pudo verificar el componente de ruta '{currentPath}': {ex.Message}");
                throw new SecurityException($"[ZipSlip] Componente de ruta no verificable: '{currentPath}'", ex);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                log($"[ZipSlip_AUDIT] Componente de ruta con Reparse Point detectado: '{currentPath}' (entrada='{entryName}')");
                throw new SecurityException($"[ZipSlip] Ruta con Reparse Point (junction/symlink) detectada en el destino: '{currentPath}'");
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
