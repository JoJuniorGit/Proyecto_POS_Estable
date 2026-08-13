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

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--targetDir" && i + 1 < args.Length) targetDir = args[i + 1];
            if (args[i] == "--serviceName" && i + 1 < args.Length) serviceName = args[i + 1];
            if (args[i] == "--package" && i + 1 < args.Length) packagePath = args[i + 1];
        }

        Console.WriteLine($"[Updater] Service Name: {serviceName}");
        Console.WriteLine($"[Updater] Target Directory: {targetDir}");
        Console.WriteLine($"[Updater] Package Path: {packagePath}");

        // Step 1: Graceful Shutdown of Windows Service
        Console.WriteLine("[Updater] Performing Graceful Shutdown of Backend Service...");
        RunCommand("nssm", $"stop \"{serviceName}\"");
        Thread.Sleep(3000); // Give processes time to release file locks

        // Step 2: Replace Binaries preserving appsettings.Production.json
        if (!string.IsNullOrWhiteSpace(packagePath) && File.Exists(packagePath))
        {
            Console.WriteLine("[Updater] Extracting update package...");
            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)) continue; // Directory entry

                // PRESERVATION RULE: Never overwrite user configuration files
                if (entry.Name.Equals("appsettings.Production.json", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.Equals("appsettings.Development.json", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[Updater] Preserving user config file: {entry.Name}");
                    continue;
                }

                string destinationPath = Path.Combine(targetDir, entry.FullName);
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
                    Console.WriteLine($"[Updater] Warning extracting {entry.Name}: {ex.Message}");
                }
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
