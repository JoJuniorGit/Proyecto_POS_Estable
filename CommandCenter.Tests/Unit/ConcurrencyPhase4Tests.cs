using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Interfaces;
using Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ConcurrencyPhase4Tests
{
    [Fact]
    public async Task ProductsController_UpdateAsync_WhenConcurrencyException_Returns409Conflict()
    {
        var inventoryMock = new Mock<IInventoryService>();
        var productMgmtMock = new Mock<IProductManagementService>();
        var currentUserMock = new Mock<ICurrentUserService>();
        currentUserMock.Setup(u => u.CanMutateCatalog).Returns(true);

        productMgmtMock.Setup(p => p.UpdateProductFromDtoAsync(It.IsAny<int>(), It.IsAny<UpdateProductDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException("Stale xmin token detected"));

        var controller = new ProductsController(inventoryMock.Object, productMgmtMock.Object, currentUserMock.Object);

        var dto = new UpdateProductDto { Id = 10, Name = "Conflicting Item" };
        var result = await controller.UpdateAsync(10, dto, CancellationToken.None);

        var conflictResult = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.Conflict, conflictResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(conflictResult.Value);
        Assert.Contains("concurrently", problemDetails.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CashDrawerController_ProcessCashAdvance_MissingIdempotencyKey_Returns400BadRequest()
    {
        var drawerMock = new Mock<ICashDrawerService>();
        var settingsMock = new Mock<ISystemSettingsService>();
        var inventoryMock = new Mock<IInventoryService>();
        var userMock = new Mock<IUserService>();
        var currentUserMock = new Mock<ICurrentUserService>();
        currentUserMock.Setup(u => u.UserId).Returns("5");
        var timeZoneProvider = new TimeZoneProvider(settingsMock.Object);
        var idempotencyMock = new Mock<IIdempotencyService>();

        var controller = new CashDrawerController(
            drawerMock.Object,
            settingsMock.Object,
            inventoryMock.Object,
            userMock.Object,
            currentUserMock.Object,
            timeZoneProvider,
            cashAdvanceCoordinator: null!,
            idempotencyMock.Object);

        // Context WITHOUT Idempotency-Key header
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var request = new CashAdvanceRequest
        {
            SessionId = 1,
            RequestedAmountLocal = 100m,
            PaymentMethodId = 1,
            PaymentMethodName = "Transferencia",
            IsTransfer = true,
            ExchangeRate = 50m
        };

        var actionResult = await controller.ProcessCashAdvanceAsync(request, CancellationToken.None);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal((int)HttpStatusCode.BadRequest, badRequestResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(badRequestResult.Value);
        Assert.Contains("Idempotency-Key", problemDetails.Detail);
    }

    [Fact]
    public async Task SupplierInvoicesController_ConfirmAsync_WhenIdempotentReplay_ShortCircuitsSuccessfully()
    {
        var supplierServiceMock = new Mock<ISupplierInvoiceService>();
        var ocrMock = new Mock<IOcrExtractionService>();
        var currentUserMock = new Mock<ICurrentUserService>();
        var idempotencyMock = new Mock<IIdempotencyService>();

        string existingKey = "CONFIRM-KEY-12345678";
        idempotencyMock.Setup(i => i.ValidateKeyFormat(existingKey, out It.Ref<string?>.IsAny))
            .Returns(true);
        idempotencyMock.Setup(i => i.ComputePayloadHash(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>()))
            .Returns(new byte[] { 1, 2, 3 });
        idempotencyMock.Setup(i => i.CheckAsync(existingKey, It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IdempotencyCheckResult.Replay(200, "{\"id\":45,\"invoiceNumber\":\"FAC-001\"}"));

        var controller = new SupplierInvoicesController(
            supplierServiceMock.Object,
            ocrMock.Object,
            currentUserMock.Object,
            idempotencyMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Idempotency-Key"] = existingKey;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var request = new ConfirmSupplierInvoiceRequestDto(Array.Empty<ConfirmLineDto>());
        var actionResult = await controller.ConfirmAsync(45, request, CancellationToken.None);

        // Should return ContentResult directly from idempotency replay cache without calling service
        var contentResult = Assert.IsType<ContentResult>(actionResult.Result);
        Assert.Contains("FAC-001", contentResult.Content);
        supplierServiceMock.Verify(s => s.ConfirmAsync(It.IsAny<int>(), It.IsAny<ConfirmSupplierInvoiceRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
