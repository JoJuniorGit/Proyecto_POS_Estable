using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CustodyPartialDeliveryControllerTests
{
    private const int ActingUserId = 5;

    private static SalesDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private static SalesController CreateController(SalesDbContext context, UserRole userRole = UserRole.Admin)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(user => user.UserId).Returns(ActingUserId.ToString());
        currentUser.Setup(user => user.UserRole).Returns(userRole);

        var salesService = new SalesService(
            context,
            Mock.Of<IInventoryService>(),
            Mock.Of<IMediator>(),
            Mock.Of<ICashDrawerService>(),
            Mock.Of<ISystemSettingsService>());

        return new SalesController(salesService, currentUser.Object, new IdempotencyService(context));
    }

    private static async Task<Sale> SeedCustodySaleAsync(
        SalesDbContext context,
        int saleId,
        decimal totalQuantity = 10m,
        decimal deliveredQuantity = 0m)
    {
        var sale = new SaleBuilder()
            .WithId(saleId)
            .WithInvoiceNumber(saleId + 1000)
            .WithCustomer(1, "Marta Ruiz", "V-12345678")
            .WithAppliedRate(40m)
            .WithStatus(SaleStatus.Completed)
            .WithDeliveryStatus(deliveredQuantity > 0m ? SaleDeliveryStatus.PartiallyDelivered : SaleDeliveryStatus.PendingPickup)
            .WithItem(7, "Test Product", totalQuantity, 2m)
            .WithItemDeliveredQuantity(1, deliveredQuantity)
            .Build();

        context.Sales.Add(sale);
        context.Users.Add(new User
        {
            Id = ActingUserId,
            Cedula = "V-87654321",
            Name = "Ana Paredes",
            FullName = "Ana Paredes de Gómez",
            Username = "ana"
        });
        await context.SaveChangesAsync();
        return sale;
    }

    private static async Task<SaleDelivery> SeedDeliveryAsync(
        SalesDbContext context,
        int saleId,
        int saleItemId = 1,
        decimal quantityDelivered = 4m)
    {
        var saleItem = await context.SaleItems.SingleAsync(item => item.Id == saleItemId);
        var delivery = new SaleDelivery
        {
            SaleId = saleId,
            DeliveredAt = new DateTime(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc),
            DeliveredByName = "Ana Paredes",
            Items = new List<SaleDeliveryItem>
            {
                new()
                {
                    SaleItemId = saleItem.Id,
                    ProductId = saleItem.ProductId,
                    ProductName = saleItem.ProductName,
                    QuantityDelivered = quantityDelivered
                }
            }
        };
        context.SaleDeliveries.Add(delivery);
        await context.SaveChangesAsync();
        return delivery;
    }

    private static PartialDeliveryRequest CreateRequest(decimal quantity, string? notes = null)
    {
        return new PartialDeliveryRequest
        {
            Items = new List<PartialDeliveryItemRequest>
            {
                new() { SaleItemId = 1, Quantity = quantity }
            },
            Notes = notes
        };
    }

    private static void AttachHttpContext(
        SalesController controller,
        string path,
        string userId,
        string? idempotencyKey,
        params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
        };
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();
        if (idempotencyKey != null)
        {
            httpContext.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static string? GetProblemMessage(object? value)
    {
        var problemDetails = Assert.IsType<ProblemDetails>(value);
        return problemDetails.Extensions.TryGetValue("message", out var message)
            ? message?.ToString()
            : null;
    }

    [Fact]
    public async Task PostDeliveries_WhenDriverHasAdminClaim_ReturnsForbiddenWithoutPersistence()
    {
        using var context = CreateContext();
        var sale = await SeedCustodySaleAsync(context, 100);
        var controller = CreateController(context, UserRole.Driver);
        AttachHttpContext(controller, $"/api/sales/{sale.Id}/deliveries", ActingUserId.ToString(), "DRIVER-DELIVERY", "Admin", "Driver");

        var result = await controller.DeliverPartialAsync(sale.Id, CreateRequest(4m));

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("Acceso denegado: no tiene permisos para confirmar esta entrega.", GetProblemMessage(forbidden.Value));
        Assert.Empty(await context.SaleDeliveries.ToListAsync());
        Assert.Empty(await context.IdempotentRequests.ToListAsync());
    }

    [Fact]
    public async Task PostDeliveries_WhenSameKeyAndPayloadAreReplayed_ReturnsOriginalReceiptWithoutSecondDelivery()
    {
        using var context = CreateContext();
        var sale = await SeedCustodySaleAsync(context, 101);
        var controller = CreateController(context);
        var request = CreateRequest(4m, "first dispatch");
        const string idempotencyKey = "DELIVERY-REPLAY-101";

        AttachHttpContext(controller, $"/api/sales/{sale.Id}/deliveries", ActingUserId.ToString(), idempotencyKey, "Admin");
        var firstHttpContext = controller.ControllerContext.HttpContext;
        var firstResult = await controller.DeliverPartialAsync(sale.Id, request);
        var firstOk = Assert.IsType<OkObjectResult>(firstResult.Result);
        var firstReceipt = Assert.IsType<DeliveryReceiptDto>(firstOk.Value);

        AttachHttpContext(controller, $"/api/sales/{sale.Id}/deliveries", ActingUserId.ToString(), idempotencyKey, "Admin");
        var replayHttpContext = controller.ControllerContext.HttpContext;
        var replayResult = await controller.DeliverPartialAsync(sale.Id, request);
        var replay = Assert.IsType<ContentResult>(replayResult.Result);
        var replayReceipt = JsonSerializer.Deserialize<DeliveryReceiptDto>(replay.Content!, JsonSerializerOptions.Web);

        Assert.Equal("MISS", firstHttpContext.Response.Headers["X-Cache-Lookup"].ToString());
        Assert.Equal("HIT", replayHttpContext.Response.Headers["X-Cache-Lookup"].ToString());
        Assert.Equal(JsonSerializer.Serialize(firstReceipt, JsonSerializerOptions.Web), replay.Content);
        Assert.Equal(firstReceipt.DeliveryId, replayReceipt?.DeliveryId);
        Assert.Single(await context.SaleDeliveries.ToListAsync());
        Assert.Equal(1, await context.IdempotentRequests.CountAsync());
    }

    [Fact]
    public async Task PostDeliveries_WhenPayloadChangesForExistingKey_Returns422WithoutSecondDelivery()
    {
        using var context = CreateContext();
        var sale = await SeedCustodySaleAsync(context, 102);
        var controller = CreateController(context);
        const string idempotencyKey = "DELIVERY-MISMATCH-102";

        AttachHttpContext(controller, $"/api/sales/{sale.Id}/deliveries", ActingUserId.ToString(), idempotencyKey, "Admin");
        var firstResult = await controller.DeliverPartialAsync(sale.Id, CreateRequest(2m, "first dispatch"));
        Assert.IsType<OkObjectResult>(firstResult.Result);

        AttachHttpContext(controller, $"/api/sales/{sale.Id}/deliveries", ActingUserId.ToString(), idempotencyKey, "Admin");
        var mismatchResult = await controller.DeliverPartialAsync(sale.Id, CreateRequest(2m, "changed notes"));

        var mismatch = Assert.IsType<ObjectResult>(mismatchResult.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, mismatch.StatusCode);
        Assert.Single(await context.SaleDeliveries.ToListAsync());
        Assert.Equal(2m, (await context.SaleItems.SingleAsync(item => item.Id == 1)).DeliveredQuantity);
    }

    [Fact]
    public async Task PostDeliveries_WhenQuantityExceedsPending_Returns400WithExactMessage()
    {
        using var context = CreateContext();
        var sale = await SeedCustodySaleAsync(context, 103, deliveredQuantity: 7m);
        var controller = CreateController(context);
        AttachHttpContext(controller, $"/api/sales/{sale.Id}/deliveries", ActingUserId.ToString(), "DELIVERY-OVER-PENDING-103", "Admin");

        var result = await controller.DeliverPartialAsync(sale.Id, CreateRequest(4m));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(
            "La cantidad a entregar supera la cantidad pendiente del producto Test Product.",
            GetProblemMessage(badRequest.Value));
        Assert.Empty(await context.SaleDeliveries.ToListAsync());
        Assert.Empty(await context.IdempotentRequests.ToListAsync());
        Assert.Equal(7m, (await context.SaleItems.SingleAsync(item => item.Id == 1)).DeliveredQuantity);
    }

    [Fact]
    public async Task PostDeliveries_WhenIdempotencyKeyIsMissing_Returns400WithoutPersistence()
    {
        using var context = CreateContext();
        var sale = await SeedCustodySaleAsync(context, 104);
        var controller = CreateController(context);
        AttachHttpContext(controller, $"/api/sales/{sale.Id}/deliveries", ActingUserId.ToString(), null, "Admin");

        var result = await controller.DeliverPartialAsync(sale.Id, CreateRequest(4m));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(
            "El encabezado Idempotency-Key es obligatorio para registrar una entrega parcial.",
            GetProblemMessage(badRequest.Value));
        Assert.Empty(await context.SaleDeliveries.ToListAsync());
        Assert.Empty(await context.IdempotentRequests.ToListAsync());
    }

    [Fact]
    public async Task ConfirmPickup_WhenServiceThrowsConcurrencyException_Returns409WithExactMessage()
    {
        var salesService = new Mock<ISalesService>();
        salesService
            .Setup(service => service.ConfirmPickupAsync(100, ActingUserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateConcurrencyException("concurrent update"));
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(user => user.UserId).Returns(ActingUserId.ToString());
        currentUser.Setup(user => user.UserRole).Returns(UserRole.Admin);
        var controller = new SalesController(salesService.Object, currentUser.Object);
        AttachHttpContext(controller, "/api/sales/100/confirm-pickup", ActingUserId.ToString(), null, "Admin");

        var result = await controller.ConfirmPickupAsync(100);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal(
            "Otro usuario modificó el retiro simultáneamente. Actualice la lista e intente de nuevo.",
            GetProblemMessage(conflict.Value));
    }

    [Fact]
    public async Task GetDeliveryNote_WhenDriverHasAdminClaim_ReturnsForbidden()
    {
        using var context = CreateContext();
        var sale = await SeedCustodySaleAsync(context, 200, deliveredQuantity: 4m);
        var delivery = await SeedDeliveryAsync(context, sale.Id);
        var controller = CreateController(context, UserRole.Driver);
        AttachHttpContext(
            controller,
            $"/api/sales/{sale.Id}/deliveries/{delivery.Id}/receipt",
            ActingUserId.ToString(),
            null,
            "Admin",
            "Driver");

        var result = await controller.GetDeliveryNotePdfAsync(sale.Id, delivery.Id);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("Acceso denegado: no tiene permisos para confirmar esta entrega.", GetProblemMessage(forbidden.Value));
    }

    [Fact]
    public async Task GetDeliveryNote_WhenDeliveryDoesNotExist_ReturnsNotFoundProblemDetails()
    {
        using var context = CreateContext();
        var sale = await SeedCustodySaleAsync(context, 201);
        var controller = CreateController(context);
        AttachHttpContext(
            controller,
            $"/api/sales/{sale.Id}/deliveries/999/receipt",
            ActingUserId.ToString(),
            null,
            "Admin");

        var result = await controller.GetDeliveryNotePdfAsync(sale.Id, 999);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Equal("Entrega no encontrada.", GetProblemMessage(notFound.Value));
    }

    [Fact]
    public async Task GetDeliveryNote_WhenDeliveryBelongsToAnotherSale_ReturnsNotFoundProblemDetails()
    {
        using var context = CreateContext();
        var sale = await SeedCustodySaleAsync(context, 202);
        var otherSale = new SaleBuilder()
            .WithId(203)
            .WithInvoiceNumber(1203)
            .WithStatus(SaleStatus.Completed)
            .WithDeliveryStatus(SaleDeliveryStatus.PartiallyDelivered)
            .WithItem(8, "Other Product", 3m, 1m)
            .Build();
        otherSale.Items.Single().Id = 2;
        context.Sales.Add(otherSale);
        await context.SaveChangesAsync();
        var delivery = await SeedDeliveryAsync(context, otherSale.Id, saleItemId: 2, quantityDelivered: 1m);
        var controller = CreateController(context);
        AttachHttpContext(
            controller,
            $"/api/sales/{sale.Id}/deliveries/{delivery.Id}/receipt",
            ActingUserId.ToString(),
            null,
            "Admin");

        var result = await controller.GetDeliveryNotePdfAsync(sale.Id, delivery.Id);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Equal("Entrega no encontrada.", GetProblemMessage(notFound.Value));
    }

    [Fact]
    public async Task GetDeliveryNote_WhenDeliveryExists_ReturnsPdfContent()
    {
        using var context = CreateContext();
        var sale = await SeedCustodySaleAsync(context, 204, deliveredQuantity: 4m);
        var delivery = await SeedDeliveryAsync(context, sale.Id);
        var controller = CreateController(context);
        AttachHttpContext(
            controller,
            $"/api/sales/{sale.Id}/deliveries/{delivery.Id}/receipt",
            ActingUserId.ToString(),
            null,
            "Admin");

        var result = await controller.GetDeliveryNotePdfAsync(sale.Id, delivery.Id);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.NotEmpty(file.FileContents);
    }
}
