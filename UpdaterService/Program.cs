using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace UpdaterService;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== POS Backend Service Auto-Updater ===");

        string targetDir = AppDomain.CurrentDomain.BaseDirectory;
        string serviceName = "PosBackendService";
        string packagePath = string.Empty;
        string expectedHash = string.Empty;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--targetDir" && i + 1 < args.Length) targetDir = args[i + 1];
            if (args[i] == "--serviceName" && i + 1 < args.Length) serviceName = args[i + 1];
            if (args[i] == "--package" && i + 1 < args.Length) packagePath = args[i + 1];
            if (args[i] == "--hash" && i + 1 < args.Length) expectedHash = args[i + 1];
        }

        Console.WriteLine($"[Updater] Service Name: {serviceName}");
        Console.WriteLine($"[Updater] Target Directory: {targetDir}");
        Console.WriteLine($"[Updater] Package Path: {packagePath}");

        // Step 0: SHA-256 Integrity Verification
        if (!string.IsNullOrWhiteSpace(packagePath) && File.Exists(packagePath))
        {
            if (string.IsNullOrWhiteSpace(expectedHash) && File.Exists(packagePath + ".sha256"))
            {
                expectedHash = File.ReadAllText(packagePath + ".sha256").Trim();
            }

            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                Console.WriteLine("[Updater] Verificando integridad SHA-256 del paquete de actualización...");
                using var fs = File.OpenRead(packagePath);
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var hashBytes = sha256.ComputeHash(fs);
                var actualHash = Convert.ToHexString(hashBytes);

                if (!actualHash.Equals(expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[Updater] ERROR CRÍTICO DE INTEGRIDAD: Hash calculado ({actualHash}) no coincide con esperado ({expectedHash}). Cancelando actualización.");
                    return;
                }

                Console.WriteLine($"[Updater] Verificación SHA-256 completada exitosamente ({actualHash}).");
            }
        }

        // Step 1: Graceful Shutdown of Windows Service
        Console.WriteLine("[Updater] Performing Graceful Shutdown of Backend Service...");
        RunCommand("nssm", $"stop \"{serviceName}\"");
        Thread.Sleep(3000); // Give processes time to release file locks

        // Step 2: Replace Binaries preserving appsettings.Production.json safely
        if (!string.IsNullOrWhiteSpace(packagePath) && File.Exists(packagePath))
        {
            try
            {
                UpdatePackageExtractor.Extract(packagePath, targetDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Updater] ERROR CRÍTICO durante la extracción del paquete: {ex.Message}");
                // No iniciar el servicio con binarios corruptos o comprometidos
                return;
            }
        }

        // Step 3: Restart Backend Service
        Console.WriteLine("[Updater] Restarting Backend Windows Service...");
        RunCommand("nssm", $"start \"{serviceName}\"");
        Console.WriteLine("[Updater] Update process completed successfully.");
    }

    static void RunCommand(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Updater] Command failed ({fileName} {arguments}): {ex.Message}");
        }
    }
}
