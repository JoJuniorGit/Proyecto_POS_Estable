using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using UpdaterService;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class VerifiedPackageCopyTests : IDisposable
{
    private readonly string _tempRoot;

    public VerifiedPackageCopyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "VerifiedPackageCopyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
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
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[UPDATER_TEST_CLEANUP] No se pudo eliminar {_tempRoot}: {ex.Message}");
        }
    }

    [Fact]
    public void CopyAndVerify_WithMatchingHash_ReturnsVerifiedCopy_UsableAfterOriginalDisappears()
    {
        string packagePath = Path.Combine(_tempRoot, "package.zip");
        CreatePackage(packagePath, "Backend.API.dll", "VERIFIED-PAYLOAD");
        string expectedHash = ComputeHash(packagePath);

        string? verifiedPath = VerifiedPackageCopy.CopyAndVerify(packagePath, expectedHash);

        Assert.NotNull(verifiedPath);
        Assert.NotEqual(packagePath, verifiedPath);
        Assert.True(File.Exists(verifiedPath));
        Assert.Equal(expectedHash, ComputeHash(verifiedPath!));

        File.Delete(packagePath);

        string targetDir = Path.Combine(_tempRoot, "target");
        Directory.CreateDirectory(targetDir);
        UpdatePackageExtractor.Extract(verifiedPath!, targetDir);

        Assert.Equal("VERIFIED-PAYLOAD", File.ReadAllText(Path.Combine(targetDir, "Backend.API.dll")));

        if (File.Exists(verifiedPath)) File.Delete(verifiedPath!);
    }

    [Fact]
    public void CopyAndVerify_WithMismatchedHash_ReturnsNull_AndLogsIntegrityError()
    {
        string packagePath = Path.Combine(_tempRoot, "package.zip");
        CreatePackage(packagePath, "Backend.API.dll", "PAYLOAD");
        var loggedMessages = new List<string>();

        string? verifiedPath = VerifiedPackageCopy.CopyAndVerify(packagePath, new string('A', 64), msg => loggedMessages.Add(msg));

        Assert.Null(verifiedPath);
        Assert.Contains(loggedMessages, m => m.Contains("ERROR CRÍTICO DE INTEGRIDAD"));
    }

    private static void CreatePackage(string packagePath, string entryName, string content)
    {
        using var zipStream = new FileStream(packagePath, FileMode.Create);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }
}
