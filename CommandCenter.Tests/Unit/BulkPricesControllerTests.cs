using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.181-T1 (S1): contrato HTTP de los endpoints bulk-prices (RBAC Admin/Manager + mapeo de errores).
public class BulkPricesControllerTests
{
    private static (ProductsController controller, Mock<IInventoryService> inventory) CreateController(bool canMutateCatalog = true)
    {
        var inventory = new Mock<IInventoryService>();
        var management = new Mock<IProductManagementService>();
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.CanMutateCatalog).Returns(canMutateCatalog);
        return (new ProductsController(inventory.Object, management.Object, user.Object), inventory);
    }

    [Fact]
    public async Task PreviewBulkPricesAsync_WhenUserCannotMutateCatalog_Returns403()
    {
        var (controller, inventory) = CreateController(canMutateCatalog: false);

        var result = await controller.PreviewBulkPricesAsync(new BulkPriceUpdateRequestDto());

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, objectResult.StatusCode);
        inventory.Verify(i => i.PreviewBulkPriceUpdateAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyBulkPricesAsync_WhenUserCannotMutateCatalog_Returns403()
    {
        var (controller, inventory) = CreateController(canMutateCatalog: false);

        var result = await controller.ApplyBulkPricesAsync(new BulkPriceUpdateRequestDto());

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, objectResult.StatusCode);
        inventory.Verify(i => i.BulkUpdatePricesAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PreviewBulkPricesAsync_WhenValid_DelegatesAndReturnsOk()
    {
        var (controller, inventory) = CreateController();
        var expected = new BulkPriceUpdateResultDto { SelectedCount = 2, UpdatedCount = 2, SkippedCount = 0 };
        inventory.Setup(i => i.PreviewBulkPriceUpdateAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var request = new BulkPriceUpdateRequestDto { ProductIds = new System.Collections.Generic.List<int> { 1, 2 } };

        var result = await controller.PreviewBulkPricesAsync(request);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    [Fact]
    public async Task ApplyBulkPricesAsync_WhenServiceThrowsArgument_Returns400()
    {
        var (controller, inventory) = CreateController();
        inventory.Setup(i => i.BulkUpdatePricesAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("El precio al mayor no puede ser mayor al precio al detal."));

        var result = await controller.ApplyBulkPricesAsync(new BulkPriceUpdateRequestDto());

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task ApplyBulkPricesAsync_WhenServiceThrowsConcurrencyConflict_Returns409()
    {
        var (controller, inventory) = CreateController();
        inventory.Setup(i => i.BulkUpdatePricesAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException("stale xmin"));

        var result = await controller.ApplyBulkPricesAsync(new BulkPriceUpdateRequestDto());

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task ApplyBulkPricesAsync_WhenServiceThrowsUnauthorized_Returns403()
    {
        var (controller, inventory) = CreateController();
        inventory.Setup(i => i.BulkUpdatePricesAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("sin permiso"));

        var result = await controller.ApplyBulkPricesAsync(new BulkPriceUpdateRequestDto());

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, objectResult.StatusCode);
    }

    [Fact]
    public async Task PreviewBulkPricesAsync_WhenProductNotFound_Returns404()
    {
        var (controller, inventory) = CreateController();
        inventory.Setup(i => i.PreviewBulkPriceUpdateAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Collections.Generic.KeyNotFoundException("Producto(s) con ID 9999 no encontrado(s)."));

        var result = await controller.PreviewBulkPricesAsync(new BulkPriceUpdateRequestDto { ProductIds = new System.Collections.Generic.List<int> { 9999 } });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }
}
