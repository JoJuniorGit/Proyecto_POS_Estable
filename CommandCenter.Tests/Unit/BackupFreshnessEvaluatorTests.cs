using System;
using System.IO;
using Backend.API.Metrics;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class BackupFreshnessEvaluatorTests
{
    [Fact]
    public void Evaluate_WithRecentDump_ReturnsFresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "backup_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var recent = Path.Combine(dir, "commandcenter_now.dump");
            File.WriteAllText(recent, "x");
            File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddMinutes(-5));

            var result = BackupFreshnessEvaluator.Evaluate(dir, 24);

            Assert.True(result.IsFresh);
            Assert.NotNull(result.LastBackupUtc);
            Assert.True(result.AgeMinutes <= 5);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Evaluate_WithStaleDump_ReturnsNotFresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "backup_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stale = Path.Combine(dir, "commandcenter_old.dump");
            File.WriteAllText(stale, "x");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

            var result = BackupFreshnessEvaluator.Evaluate(dir, 24);

            Assert.False(result.IsFresh);
            Assert.NotNull(result.LastBackupUtc);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Evaluate_WithMissingDirectory_ReturnsMissing()
    {
        var result = BackupFreshnessEvaluator.Evaluate(@"C:\ruta\inexistente\backups", 24);

        Assert.False(result.IsFresh);
        Assert.Null(result.LastBackupUtc);
        Assert.Null(result.AgeMinutes);
    }
}