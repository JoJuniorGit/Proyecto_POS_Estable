using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class UpdateUrlPolicyTests
{
    [Theory]
    [InlineData("https://updates.example.com/pos/")]
    [InlineData("https://192.168.1.50/updates/")]
    [InlineData("http://localhost:5000/updates/")]
    [InlineData("http://127.0.0.1:5000/updates/")]
    [InlineData("http://[::1]:5000/updates/")]
    public void IsAllowed_AllowsHttpsAndLoopbackHttp(string url)
    {
        Assert.True(UpdateUrlPolicy.IsAllowed(url));
    }

    [Theory]
    [InlineData("http://192.168.1.50/updates/")]
    [InlineData("http://updates.example.com/pos/")]
    [InlineData("ftp://updates.example.com/pos/")]
    [InlineData("file:///C:/updates/pos.exe")]
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsAllowed_RejectsNonLoopbackHttpAndInvalidInput(string? url)
    {
        Assert.False(UpdateUrlPolicy.IsAllowed(url));
    }
}
