using Backend.API.Controllers;
using Core.DTOs;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 15 (S8): el controlador traduce los errores de validacion del servicio a 400 sin filtrar
/// detalles internos y conserva los contratos de lectura.
/// </summary>
public class InventoryRotationControllerTests
{
    [Fact]
    public async Task GetReportAsync_ReturnsOkWithServiceReport()
    {
        var service = new Mock<IInventoryRotationService>();
        service
            .Setup(s => s.GetReportAsync(It.IsAny<InventoryRotationFilterDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InventoryRotationReportDto { TotalCount = 2 });
        var controller = new InventoryRotationController(service.Object);

        var result = await controller.GetReportAsync(new InventoryRotationFilterDto());

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var report = Assert.IsType<InventoryRotationReportDto>(ok.Value);
        Assert.Equal(2, report.TotalCount);
    }

    [Fact]
    public async Task GetReportAsync_MapsArgumentExceptionToBadRequest()
    {
        var service = new Mock<IInventoryRotationService>();
        service
            .Setup(s => s.GetReportAsync(It.IsAny<InventoryRotationFilterDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("La ventana de ventas debe estar entre 1 y 365 dias."));
        var controller = new InventoryRotationController(service.Object);

        var result = await controller.GetReportAsync(new InventoryRotationFilterDto());

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetDeadStockProductIdsAsync_ReturnsSelection()
    {
        var service = new Mock<IInventoryRotationService>();
        service
            .Setup(s => s.GetDeadStockProductIdsAsync(It.IsAny<InventoryRotationFilterDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeadStockSelectionDto
            {
                ProductIds = new[] { 4, 9 },
                TotalCount = 2
            });
        var controller = new InventoryRotationController(service.Object);

        var result = await controller.GetDeadStockProductIdsAsync(new InventoryRotationFilterDto());

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var selection = Assert.IsType<DeadStockSelectionDto>(ok.Value);
        Assert.Equal(new[] { 4, 9 }, selection.ProductIds);
    }
}
