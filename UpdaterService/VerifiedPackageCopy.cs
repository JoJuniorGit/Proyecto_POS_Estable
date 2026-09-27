using System;
using System.IO;
using System.Security.Cryptography;

namespace UpdaterService;

public static class VerifiedPackageCopy
{
    public static string? CopyAndVerify(string packagePath, string expectedHash, Action<string>? logAction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);

        var log = logAction ?? Console.WriteLine;
        string verifiedPackagePath = Path.Combine(Path.GetTempPath(), "PosUpdaterPackage_" + Guid.NewGuid().ToString("N") + ".pkg");

        log("[Updater] Verificando integridad SHA-256 del paquete de actualización...");

        string actualHash;
        try
        {
            using var source = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = new FileStream(verifiedPackagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var sha256 = SHA256.Create();

            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                destination.Write(buffer, 0, bytesRead);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            actualHash = Convert.ToHexString(sha256.Hash!);
        }
        catch (Exception ex)
        {
            log($"[Updater] ERROR CRÍTICO DE INTEGRIDAD: No se pudo copiar el paquete para verificación: {ex.Message}");
            TryDeleteFile(verifiedPackagePath);
            return null;
        }

        if (!actualHash.Equals(expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            log($"[Updater] ERROR CRÍTICO DE INTEGRIDAD: Hash calculado ({actualHash}) no coincide con esperado ({expectedHash}). Cancelando actualización.");
            TryDeleteFile(verifiedPackagePath);
            return null;
        }

        log($"[Updater] Verificación SHA-256 completada exitosamente ({actualHash}).");
        return verifiedPackagePath;
    }

    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
