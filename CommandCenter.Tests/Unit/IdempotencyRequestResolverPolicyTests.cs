using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Services;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class IdempotencyRequestResolverPolicyTests
{
    [Fact]
    public async Task ResolveWithPolicyAsync_CompatibilityModeAllowsMissingKey()
    {
        var resolver = CreateResolver();
        var controller = CreateController();

        var result = await resolver.ResolveWithPolicyAsync(
            controller,
            "/api/cashdrawer/open-session",
            "actor|payload",
            requireKey: false);

        Assert.False(result.ShouldStop);
        Assert.Null(result.Key);
    }

    [Fact]
    public async Task ResolveWithPolicyAsync_EnforcementModeRejectsMissingKey()
    {
        var resolver = CreateResolver();
        var controller = CreateController();

        var result = await resolver.ResolveWithPolicyAsync(
            controller,
            "/api/cashdrawer/open-session",
            "actor|payload",
            requireKey: true);

        Assert.True(result.ShouldStop);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.BlockingResult);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }

    private static IdempotencyRequestResolver CreateResolver()
        => new(Mock.Of<IIdempotencyService>(), Mock.Of<ICurrentUserService>());

    private static TestController CreateController()
        => new()
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    private sealed class TestController : ControllerBase
    {
    }
}
