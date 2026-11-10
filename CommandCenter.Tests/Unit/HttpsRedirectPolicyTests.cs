using System.Net;
using Backend.API.Startup;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class HttpsRedirectPolicyTests
{
    [Fact]
    public void ShouldRedirect_RemoteHttpRequest()
    {
        var context = CreateContext("http", IPAddress.Parse("192.168.1.25"));

        Assert.True(HttpsRedirectPolicy.ShouldRedirect(context));
    }

    [Fact]
    public void ShouldNotRedirect_HttpsRequest()
    {
        var context = CreateContext("https", IPAddress.Parse("192.168.1.25"));

        Assert.False(HttpsRedirectPolicy.ShouldRedirect(context));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void ShouldNotRedirect_LoopbackHttpRequest(string remoteIp)
    {
        var context = CreateContext("http", IPAddress.Parse(remoteIp));

        Assert.False(HttpsRedirectPolicy.ShouldRedirect(context));
    }

    [Fact]
    public void ShouldNotRedirect_TestServerRequestWithoutRemoteIp()
    {
        var context = CreateContext("http", remoteIp: null);

        Assert.False(HttpsRedirectPolicy.ShouldRedirect(context));
    }

    [Fact]
    public void BuildTarget_PreservesHostPathAndQueryAndUsesHttpsPort()
    {
        var context = CreateContext("http", IPAddress.Parse("192.168.1.25"));
        context.Request.Host = new HostString("pos.local", 5000);
        context.Request.Path = "/api/sales/5/complete";
        context.Request.QueryString = new QueryString("?mode=manual");

        var target = HttpsRedirectPolicy.BuildTarget(context, 5001);

        Assert.Equal("https", target.Scheme);
        Assert.Equal("pos.local", target.Host);
        Assert.Equal(5001, target.Port);
        Assert.Equal("/api/sales/5/complete", target.AbsolutePath);
        Assert.Equal("?mode=manual", target.Query);
    }

    private static DefaultHttpContext CreateContext(string scheme, IPAddress? remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString("192.168.1.25", 5000);
        context.Connection.RemoteIpAddress = remoteIp;
        return context;
    }
}
