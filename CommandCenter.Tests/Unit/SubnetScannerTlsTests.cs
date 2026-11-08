using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Regression for the automatic/manual server probe: the certificate callback must be attached to
/// an <see cref="HttpClientHandler"/> so it receives the <see cref="HttpRequestMessage"/> and can
/// resolve the TOFU host pin. The previous SocketsHttpHandler SslOptions callback received an
/// SslStream, so self-signed local servers always failed the probe.
/// </summary>
public class SubnetScannerTlsTests
{
    [Fact]
    public void CreateScannerHandler_ExposesRequestAwareCertificateCallback()
    {
        using var handler = SubnetScannerService.CreateScannerHandler();
        var callback = handler.ServerCertificateCustomValidationCallback;

        Assert.NotNull(callback);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:5001/api/health");
        Assert.True(callback!(request, null, null, SslPolicyErrors.None));
    }

    [Fact]
    public void ScannerSource_UsesHttpClientHandlerInsteadOfSslOptionsCallback()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop.Client.Core",
            "Services",
            "SubnetScannerService.cs"));

        Assert.Contains("ServerCertificateCustomValidationCallback", source, StringComparison.Ordinal);
        Assert.Contains("new HttpClientHandler()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SocketsHttpHandler", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "CommandCenter.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root with CommandCenter.slnx was not found.");
    }
}
