using UpdaterService;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class UpdaterServiceNamePolicyTests
{
    [Theory]
    [InlineData("PosBackendService")]
    [InlineData("posbackendservice")]
    [InlineData(" PosBackendService ")]
    public void IsAllowed_AllowsCanonicalServiceName(string serviceName)
    {
        Assert.True(ServiceNamePolicy.IsAllowed(serviceName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("OtherService")]
    [InlineData("PosBackendService\" --malicious")]
    [InlineData("PosBackendService --extra")]
    public void IsAllowed_RejectsUnknownOrMalformedNames(string? serviceName)
    {
        Assert.False(ServiceNamePolicy.IsAllowed(serviceName));
    }
}
