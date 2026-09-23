using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ServerPortResolverTests
{
    [Theory]
    [InlineData("https://192.168.1.50/", 5001)]
    [InlineData("https://192.168.1.50:5001/", 5001)]
    [InlineData("http://localhost:5000/", 5000)]
    [InlineData("http://192.168.1.50/", 5000)]
    [InlineData("https://192.168.1.50:443/", 443)]
    [InlineData("https://192.168.1.50:7443/", 7443)]
    [InlineData("http://192.168.1.50:8080/", 8080)]
    [InlineData("192.168.1.50", 5001)]
    [InlineData("servidor.local", 5001)]
    public void Resolve_WithValidAddress_ReturnsSchemeDefaultOrExplicitPort(string address, int expectedPort)
    {
        Assert.Equal(expectedPort, ServerPortResolver.Resolve(address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("https:// bad host")]
    public void Resolve_WithInvalidAddress_ReturnsHttpsDefault(string? address)
    {
        Assert.Equal(ServerPortResolver.HttpsPort, ServerPortResolver.Resolve(address));
    }

    [Fact]
    public void Resolve_WithHttpDefaultPort_DoesNotReturnWebDefault()
    {
        Assert.Equal(ServerPortResolver.HttpPort, ServerPortResolver.Resolve("http://192.168.1.50/"));
        Assert.NotEqual(80, ServerPortResolver.Resolve("http://192.168.1.50/"));
    }

    [Fact]
    public void Resolve_WithHttpsDefaultPort_DoesNotReturnWebDefault()
    {
        Assert.Equal(ServerPortResolver.HttpsPort, ServerPortResolver.Resolve("https://192.168.1.50/"));
        Assert.NotEqual(443, ServerPortResolver.Resolve("https://192.168.1.50/"));
    }
}
