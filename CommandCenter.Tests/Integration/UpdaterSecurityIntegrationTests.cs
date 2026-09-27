using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using UpdaterService;
using Xunit;

namespace CommandCenter.Tests.Integration;

public class UpdaterSecurityIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _targetDir;
    private readonly string _outsideDir;

    public UpdaterSecurityIntegrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "UpdaterSecTests_" + Guid.NewGuid().ToString("N"));
        _targetDir = Path.Combine(_tempRoot, "Target");
        _outsideDir = Path.Combine(_tempRoot, "Outside");

        Directory.CreateDirectory(_targetDir);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        // 8.9-L3: no tragar en silencio: se registra el error de limpieza para diagnóstico,
        // aunque la prueba ya haya terminado (limpieza best-effort del directorio temporal).
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[UPDATER_TEST_CLEANUP] No se pudo eliminar {_tempRoot}: {ex.Message}");
        }
    }

    [Fact]
    public void Extract_WithRelativePathTraversal_ThrowsSecurityException_AndDoesNotWriteOutside()
    {
        // Arrange
        string zipPath = Path.Combine(_tempRoot, "malicious_relative.zip");
        string outsideEscapedFile = Path.Combine(_tempRoot, "evil_relative.txt");

        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var maliciousEntry = archive.CreateEntry("../evil_relative.txt");
            using var writer = new StreamWriter(maliciousEntry.Open());
            writer.Write("MALICIOUS CONTENT");
        }

        var loggedMessages = new System.Collections.Generic.List<string>();

        // Act & Assert
        var ex = Assert.Throws<SecurityException>(() =>
            UpdatePackageExtractor.Extract(zipPath, _targetDir, msg => loggedMessages.Add(msg)));

        Assert.Contains("[ZipSlip]", ex.Message);
        Assert.False(File.Exists(outsideEscapedFile), "El archivo malicioso NO debe existir fuera del directorio de destino.");
        Assert.Contains(loggedMessages, m => m.Contains("[ZipSlip_AUDIT]"));
    }

    [Fact]
    public void Extract_WithRootedAbsolutePath_ThrowsSecurityException()
    {
        // Arrange
        string zipPath = Path.Combine(_tempRoot, "malicious_absolute.zip");

        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var maliciousEntry = archive.CreateEntry(@"C:\Windows\System32\malicious_cmd.exe");
            using var writer = new StreamWriter(maliciousEntry.Open());
            writer.Write("MALICIOUS ABSOLUTE CONTENT");
        }

        var loggedMessages = new System.Collections.Generic.List<string>();

        // Act & Assert
        var ex = Assert.Throws<SecurityException>(() =>
            UpdatePackageExtractor.Extract(zipPath, _targetDir, msg => loggedMessages.Add(msg)));

        Assert.Contains("[ZipSlip]", ex.Message);
        Assert.Contains(loggedMessages, m => m.Contains("[ZipSlip_AUDIT]"));
    }

    [Fact]
    public void Extract_PreservesRootAppSettingsProduction_DoesNotOverwriteExisting()
    {
        // Arrange
        string targetConfigFile = Path.Combine(_targetDir, "appsettings.Production.json");
        File.WriteAllText(targetConfigFile, "{ \"Preserved\": true }");

        string zipPath = Path.Combine(_tempRoot, "package_with_root_config.zip");
        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var configEntry = archive.CreateEntry("appsettings.Production.json");
            using (var writer = new StreamWriter(configEntry.Open()))
            {
                writer.Write("{ \"Overwritten\": true }");
            }

            var normalEntry = archive.CreateEntry("Backend.API.dll");
            using (var normalWriter = new StreamWriter(normalEntry.Open()))
            {
                normalWriter.Write("BINARY");
            }
        }

        // Act
        UpdatePackageExtractor.Extract(zipPath, _targetDir);

        // Assert
        string currentContent = File.ReadAllText(targetConfigFile);
        Assert.Contains("\"Preserved\": true", currentContent);
        Assert.True(File.Exists(Path.Combine(_targetDir, "Backend.API.dll")));
    }

    [Fact]
    public void Extract_ExtractsSubdirectoryAppSettingsProduction_BecauseNotAtRoot()
    {
        // Arrange: Subcarpeta legítima que contiene un archivo llamado appsettings.Production.json
        string zipPath = Path.Combine(_tempRoot, "package_with_sub_config.zip");
        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var subEntry = archive.CreateEntry("subdir/appsettings.Production.json");
            using var writer = new StreamWriter(subEntry.Open());
            writer.Write("{ \"SubConfig\": true }");
        }

        // Act
        UpdatePackageExtractor.Extract(zipPath, _targetDir);

        // Assert
        string extractedSubConfig = Path.Combine(_targetDir, "subdir", "appsettings.Production.json");
        Assert.True(File.Exists(extractedSubConfig), "El archivo en subcarpeta debe ser extraído normalmente.");
        Assert.Contains("\"SubConfig\": true", File.ReadAllText(extractedSubConfig));
    }

    [Fact]
    public void Extract_WithSymlinkAttributes_SkipsExtractionSafely()
    {
        // Arrange
        string zipPath = Path.Combine(_tempRoot, "package_with_symlink.zip");
        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var symlinkEntry = archive.CreateEntry("link_outside.txt");
            // Unix symlink mode: 0xA1FF0000
            symlinkEntry.ExternalAttributes = (int)(0xA1FF << 16);
            using var writer = new StreamWriter(symlinkEntry.Open());
            writer.Write("/etc/passwd");
        }

        var loggedMessages = new System.Collections.Generic.List<string>();

        // Act
        UpdatePackageExtractor.Extract(zipPath, _targetDir, msg => loggedMessages.Add(msg));

        // Assert
        string targetFile = Path.Combine(_targetDir, "link_outside.txt");
        Assert.False(File.Exists(targetFile), "El enlace simbólico debe ser omitido.");
        Assert.Contains(loggedMessages, m => m.Contains("Enlace simbólico"));
    }

    [Fact]
    public void Extract_WithAlternateDataStreamEntryName_ThrowsSecurityException()
    {
        string zipPath = Path.Combine(_tempRoot, "malicious_ads.zip");
        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var adsEntry = archive.CreateEntry("Backend.API.dll:hidden");
            using var writer = new StreamWriter(adsEntry.Open());
            writer.Write("ADS CONTENT");
        }

        var loggedMessages = new System.Collections.Generic.List<string>();

        var ex = Assert.Throws<SecurityException>(() =>
            UpdatePackageExtractor.Extract(zipPath, _targetDir, msg => loggedMessages.Add(msg)));

        Assert.Contains("[ZipSlip]", ex.Message);
        Assert.Contains(loggedMessages, m => m.Contains("[ZipSlip_AUDIT]"));
        Assert.False(File.Exists(Path.Combine(_targetDir, "Backend.API.dll")));
    }

    [Fact]
    public void Extract_WithJunctionComponentInsideTarget_ThrowsSecurityException_AndDoesNotWriteOutside()
    {
        string junctionPath = Path.Combine(_targetDir, "linked");
        if (!TryCreateJunction(junctionPath, _outsideDir))
        {
            return;
        }

        string zipPath = Path.Combine(_tempRoot, "malicious_junction.zip");
        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var escapedEntry = archive.CreateEntry("linked/evil.dll");
            using var writer = new StreamWriter(escapedEntry.Open());
            writer.Write("MALICIOUS CONTENT");
        }

        var loggedMessages = new System.Collections.Generic.List<string>();

        try
        {
            var ex = Assert.Throws<SecurityException>(() =>
                UpdatePackageExtractor.Extract(zipPath, _targetDir, msg => loggedMessages.Add(msg)));

            Assert.Contains("[ZipSlip]", ex.Message);
            Assert.Contains(loggedMessages, m => m.Contains("[ZipSlip_AUDIT]"));
            Assert.False(File.Exists(Path.Combine(_outsideDir, "evil.dll")), "No debe escribirse a través del junction.");
        }
        finally
        {
            TryDeleteJunction(junctionPath);
        }
    }

    [Fact]
    public void Extract_WhenCommitFails_RestoresReplacedBinaries_AndLogsRollback()
    {
        string replacementTarget = Path.Combine(_targetDir, "aaa_payload.dll");
        File.WriteAllText(replacementTarget, "ORIGINAL-BINARY");

        string collisionTarget = Path.Combine(_targetDir, "zzz_collision.dll");
        Directory.CreateDirectory(collisionTarget);

        string zipPath = Path.Combine(_tempRoot, "package_commit_failure.zip");
        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var firstEntry = archive.CreateEntry("aaa_payload.dll");
            using (var firstWriter = new StreamWriter(firstEntry.Open()))
            {
                firstWriter.Write("UPDATED-BINARY");
            }

            var collidingEntry = archive.CreateEntry("zzz_collision.dll");
            using (var collidingWriter = new StreamWriter(collidingEntry.Open()))
            {
                collidingWriter.Write("SHOULD-NOT-REPLACE-DIRECTORY");
            }
        }

        var loggedMessages = new System.Collections.Generic.List<string>();

        var ex = Record.Exception(() =>
            UpdatePackageExtractor.Extract(zipPath, _targetDir, msg => loggedMessages.Add(msg)));

        Assert.NotNull(ex);
        Assert.Equal("ORIGINAL-BINARY", File.ReadAllText(replacementTarget));
        Assert.True(Directory.Exists(collisionTarget), "El directorio preexistente no debe ser reemplazado por un archivo.");
        Assert.Contains(loggedMessages, m => m.Contains("Restaurando respaldo"));
    }

    private static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process == null) return false;
        if (!process.WaitForExit(10000)) return false;
        return process.ExitCode == 0 && Directory.Exists(junctionPath);
    }

    private static void TryDeleteJunction(string junctionPath)
    {
        try
        {
            if (Directory.Exists(junctionPath)) Directory.Delete(junctionPath);
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[UPDATER_TEST_CLEANUP] No se pudo eliminar el junction {junctionPath}: {ex.Message}");
        }
    }
}
