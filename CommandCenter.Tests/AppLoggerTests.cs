using System;
using System.IO;
using Core.Logging;
using Xunit;

namespace CommandCenter.Tests;

public class AppLoggerTests
{
    [Fact]
    public async Task LogStart_CreatesStartLogFile_WithTimestampedContent()
    {
        AppLogger.LogStart("Test startup message");
        await AppLogger.FlushAsync();

        Assert.True(File.Exists(AppLogger.StartLogPath));
        var content = File.ReadAllText(AppLogger.StartLogPath);
        Assert.Contains("[START]", content);
        Assert.Contains("Test startup message", content);
    }

    [Fact]
    public async Task LogCrash_CreatesCrashLogFile_WithExceptionDetails()
    {
        try
        {
            throw new InvalidOperationException("Test database connection exception");
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash(ex, "UnitTest.Context");
        }

        await AppLogger.FlushAsync();

        Assert.True(File.Exists(AppLogger.CrashLogPath));
        var content = File.ReadAllText(AppLogger.CrashLogPath);
        Assert.Contains("[CRASH]", content);
        Assert.Contains("UnitTest.Context", content);
        Assert.Contains("Test database connection exception", content);
    }
}
