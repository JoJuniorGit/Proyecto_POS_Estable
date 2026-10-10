using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Core.Logging;
using Xunit;

namespace CommandCenter.Tests;

public class AsyncLoggingTests
{
    [Fact]
    public async Task LogStart_BurstAfterFlush_PreservesFifoOrder()
    {
        var runId = Guid.NewGuid().ToString("N");
        var markers = new string[50];
        for (var i = 0; i < markers.Length; i++)
        {
            markers[i] = $"ASYNC-BURST-{runId}-{i:D2}";
            AppLogger.LogStart(markers[i]);
        }

        await AppLogger.FlushAsync();

        var content = ReadAllTextShared(AppLogger.StartLogPath);
        var previousIndex = -1;
        foreach (var marker in markers)
        {
            var index = content.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(
                index > previousIndex,
                $"Marker '{marker}' missing or out of order (index={index}, previousIndex={previousIndex}).");
            previousIndex = index;
        }
    }

    [Fact]
    public async Task LogDbError_AfterFlush_WritesToDbErrorsAndCrashLogs()
    {
        var marker = $"ASYNC-DBERR-{Guid.NewGuid():N}";

        AppLogger.LogDbError(marker, "AsyncLoggingTests");
        await AppLogger.FlushAsync();

        Assert.Contains(marker, ReadAllTextShared(AppLogger.DbErrorsLogPath));
        Assert.Contains(marker, ReadAllTextShared(AppLogger.CrashLogPath));
    }

    [Fact]
    public async Task ClientStateLogger_AfterFlush_WritesContent()
    {
        var marker = $"ASYNC-CLIENT-{Guid.NewGuid():N}";

        ClientStateLogger.LogInfo(marker, "AsyncLoggingTests");
        await ClientStateLogger.FlushAsync();

        Assert.Contains(marker, ReadAllTextShared(ClientStateLogger.ResilienceLogPath));
    }

    [Fact]
    public async Task AppLogger_AfterFlush_PreservesLineFormat()
    {
        var marker = $"ASYNC-FORMAT-{Guid.NewGuid():N}";

        AppLogger.LogStart(marker);
        await AppLogger.FlushAsync();

        var content = ReadAllTextShared(AppLogger.StartLogPath);
        Assert.Matches(
            @"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] \[START\] " + Regex.Escape(marker),
            content);
        Assert.Contains($"{marker}\n----------------------------------------\n", content);
    }

    [Fact]
    public async Task ClientStateLogger_AfterFlush_PreservesLineFormat()
    {
        var marker = $"ASYNC-ISO-{Guid.NewGuid():N}";

        ClientStateLogger.LogInfo(marker, "AsyncLoggingTests");
        await ClientStateLogger.FlushAsync();

        var content = ReadAllTextShared(ClientStateLogger.ResilienceLogPath);
        Assert.Matches(
            @"\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] \[INFO\] \[AsyncLoggingTests\] " + Regex.Escape(marker) + @"\n",
            content);
    }

    private static string ReadAllTextShared(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
