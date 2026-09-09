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

        // Step 0: SHA-256 Integrity Verification (fail-closed — 8.20-A03/8U-N2)
        // Sin paquete o sin hash esperado la actualización se ABORTA: nunca se extrae ni
        // se reinicia el servicio con binarios no verificados.
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            Console.WriteLine("[Updater] ERROR CRÍTICO: No se proporcionó un paquete de actualización. Cancelando.");
            return;
        }

        if (string.IsNullOrWhiteSpace(expectedHash) && File.Exists(packagePath + ".sha256"))
        {
            expectedHash = File.ReadAllText(packagePath + ".sha256").Trim();
        }

        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            Console.WriteLine("[Updater] ERROR CRÍTICO DE INTEGRIDAD: No se proporcionó hash esperado (ni --hash ni sidecar .sha256). Fail-closed: cancelando la actualización.");
            return;
        }

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

        // Step 1: Graceful Shutdown of Windows Service
        Console.WriteLine("[Updater] Performing Graceful Shutdown of Backend Service...");
        ControlService("stop", serviceName, targetDir);
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
        ControlService("start", serviceName, targetDir);
        Console.WriteLine("[Updater] Update process completed successfully.");
    }

    static void ControlService(string action, string serviceName, string? targetDir)
    {
        var (cmd, args) = ResolveServiceManagerCommand(action, serviceName, targetDir);
        RunCommand(cmd, args);
    }

    public static (string fileName, string arguments) ResolveServiceManagerCommand(string action, string serviceName, string? targetDir)
    {
        // Prioridad 1: AppContext.BaseDirectory/nssm.exe
        var appDirNssm = Path.Combine(AppContext.BaseDirectory, "nssm.exe");
        if (File.Exists(appDirNssm))
        {
            return (appDirNssm, $"{action} \"{serviceName}\"");
        }

        // Prioridad 2: targetDir/nssm.exe o targetDir/tools/nssm.exe
        if (!string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir))
        {
            var targetNssm = Path.Combine(targetDir, "nssm.exe");
            if (File.Exists(targetNssm)) return (targetNssm, $"{action} \"{serviceName}\"");

            var toolsNssm = Path.Combine(targetDir, "tools", "nssm.exe");
            if (File.Exists(toolsNssm)) return (toolsNssm, $"{action} \"{serviceName}\"");
        }

        // Prioridad 3: nssm en PATH si existe
        if (IsBinaryOnPath("nssm.exe") || IsBinaryOnPath("nssm"))
        {
            return ("nssm", $"{action} \"{serviceName}\"");
        }

        // Fallback documentado: sc.exe nativo de Windows (solo stop/start)
        Console.WriteLine($"[Updater] [AVISO] Binario nssm.exe no encontrado en '{AppContext.BaseDirectory}' ni en PATH. Usando fallback nativo sc.exe (limitado a inicio y detención).");
        return ("sc.exe", $"{action} \"{serviceName}\"");
    }

    private static bool IsBinaryOnPath(string binaryName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return false;

        foreach (var path in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(path.Trim(), binaryName);
                if (File.Exists(full)) return true;
            }
            catch { }
        }
        return false;
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

