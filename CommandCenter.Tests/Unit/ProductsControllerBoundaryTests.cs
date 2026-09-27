using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ProductsControllerBoundaryTests
{
    [Fact]
    public void ProductsController_DeclaresExactlyOnePublicConstructor()
    {
        var publicConstructors = typeof(ProductsController)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        var constructor = Assert.Single(publicConstructors);
        Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IProductManagementService));
    }

    [Fact]
    public async Task ProductsController_GetByIdAsync_MissingId_ReturnsNotFoundWithoutEntityFallback()
    {
        var inventory = new Mock<IInventoryService>();
        var management = new Mock<IProductManagementService>();
        management
            .Setup(service => service.GetProductDtoByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProductDto?)null);

        var controller = new ProductsController(inventory.Object, management.Object, Mock.Of<ICurrentUserService>());

        var result = await controller.GetByIdAsync(999);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        inventory.VerifyNoOtherCalls();
    }
}
