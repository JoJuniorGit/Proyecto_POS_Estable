using System;
using System.IO;
using System.Linq;

namespace Backend.API.Metrics;

public static class BackupFreshnessEvaluator
{
    public static BackupFreshnessResult Evaluate(string? backupDirectory, double? maxAgeHours)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(backupDirectory)
                ? "C:\\Backups\\CommandCenter"
                : backupDirectory;
            var threshold = maxAgeHours ?? 24;

            if (!Directory.Exists(dir))
            {
                return BackupFreshnessResult.Missing;
            }

            var newest = new DirectoryInfo(dir)
                .GetFiles("*.dump")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null)
            {
                return BackupFreshnessResult.Missing;
            }

            var ageMinutes = (DateTime.UtcNow - newest.LastWriteTimeUtc).TotalMinutes;
            return new BackupFreshnessResult(
                newest.LastWriteTimeUtc,
                Math.Round(ageMinutes, 0),
                ageMinutes <= threshold * 60);
        }
        catch
        {
            return BackupFreshnessResult.Missing;
        }
    }
}

public sealed record BackupFreshnessResult(DateTime? LastBackupUtc, double? AgeMinutes, bool IsFresh)
{
    public static BackupFreshnessResult Missing => new(null, null, false);
}