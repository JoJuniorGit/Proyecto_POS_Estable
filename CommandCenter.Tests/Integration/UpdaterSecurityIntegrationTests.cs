using System;
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
}
