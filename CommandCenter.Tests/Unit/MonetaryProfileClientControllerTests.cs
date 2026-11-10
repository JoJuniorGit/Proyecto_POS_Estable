using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T4b1 (S9): contrato del endpoint read-only de perfil monetario para clientes.
// Cualquier usuario autenticado (incluido Cashier) puede leerlo; la superficie no expone
// ninguna mutación y el fallo de composición del servicio es fail-closed.
public sealed class MonetaryProfileClientControllerTests
{
    private static MonetaryProfileDto ConfiguredSingleProfile() => new()
    {
        Mode = MonetaryMode.Single,
        PrimaryCurrencyCode = "EUR",
        PrimaryCurrencySymbol = "€",
        SecondaryCurrencyCode = null,
        SecondaryCurrencySymbol = null,
        IsConfigured = true
    };

    [Fact]
    public async Task GetMonetaryProfileAsync_ReturnsOkWithServiceProfile()
    {
        var expected = ConfiguredSingleProfile();
        var cancellationToken = new CancellationTokenSource().Token;
        var service = new Mock<IMonetaryProfileService>();
        service
            .Setup(s => s.GetAsync(cancellationToken))
            .ReturnsAsync(expected);
        var controller = new MonetaryProfileClientController(service.Object);

        var result = await controller.GetMonetaryProfileAsync(cancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
        service.Verify(s => s.GetAsync(cancellationToken), Times.Once);
    }

    [Fact]
    public void Constructor_WithoutService_FailsClosed()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new MonetaryProfileClientController(monetaryProfileService: null));

        Assert.Contains("perfil monetario", exception.Message);
    }

    [Fact]
    public void Controller_AllowsAnyAuthenticatedRole_AndExposesOnlyGet()
    {
        var controllerType = typeof(MonetaryProfileClientController);

        var authorize = controllerType.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Null(authorize!.Roles);

        Assert.NotNull(controllerType.GetCustomAttribute<ApiControllerAttribute>());

        var route = controllerType.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("api/client", route!.Template);

        var httpAttributes = controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .ToArray();

        Assert.Single(httpAttributes);
        var get = Assert.IsType<HttpGetAttribute>(httpAttributes[0]);
        Assert.Equal("monetary-profile", get.Template);
    }
}
