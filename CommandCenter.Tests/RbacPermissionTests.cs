using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommandCenter.Tests;

public class MockCurrentUserService : ICurrentUserService
{
    public UserRole? UserRole { get; set; }
    public string? UserId { get; set; }
    public bool CanMutateCatalog => UserRole == Core.Entities.UserRole.Admin;
    public bool CanMutateSettings => UserRole == Core.Entities.UserRole.Admin;
    public bool CanMutateExchangeRate => UserRole == Core.Entities.UserRole.Admin;
}

public class RbacPermissionTests
{
    private InventoryDbContext GetInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task NullRole_ThrowsUnauthorizedAccessException_OnCreateProduct()
    {
        using var db = GetInMemoryInventoryDbContext();
        var nullService = new MockCurrentUserService { UserRole = null };
        var inventoryService = new InventoryService(db, nullService);

        var product = new Product { Name = "Soda", SKU = "100000", CostPriceUSD = 1.00m, PriceRetailUSD = 1.50m };

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inventoryService.CreateProductAsync(product));
        Assert.Contains("El usuario actual no tiene permisos para modificar el catálogo", ex.Message);
    }

    [Fact]
    public async Task CashierRole_ThrowsUnauthorizedAccessException_OnCreateProduct()
    {
        using var db = GetInMemoryInventoryDbContext();
        var cashierService = new MockCurrentUserService { UserRole = UserRole.Cashier };
        var inventoryService = new InventoryService(db, cashierService);

        var product = new Product { Name = "Soda", SKU = "100001", CostPriceUSD = 1.00m, PriceRetailUSD = 1.50m };

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inventoryService.CreateProductAsync(product));
        Assert.Contains("El usuario actual no tiene permisos para modificar el catálogo", ex.Message);
    }

    [Fact]
    public async Task AdminRole_AllowsCreateProduct()
    {
        using var db = GetInMemoryInventoryDbContext();
        var adminService = new MockCurrentUserService { UserRole = UserRole.Admin };
        var inventoryService = new InventoryService(db, adminService);

        var product = new Product { Name = "Soda", SKU = "100002", CostPriceUSD = 1.00m, PriceRetailUSD = 1.50m };

        var created = await inventoryService.CreateProductAsync(product);
        Assert.NotNull(created);
        Assert.Equal("100002", created.SKU);
    }

    [Fact]
    public async Task CashierRole_ThrowsUnauthorizedAccessException_OnDeleteProduct()
    {
        using var db = GetInMemoryInventoryDbContext();
        var adminService = new MockCurrentUserService { UserRole = UserRole.Admin };
        var inventoryService = new InventoryService(db, adminService);

        var product = new Product { Name = "Juice", SKU = "100003", CostPriceUSD = 1.00m, PriceRetailUSD = 1.50m };
        await inventoryService.CreateProductAsync(product);

        var cashierService = new MockCurrentUserService { UserRole = UserRole.Cashier };
        var cashierInventoryService = new InventoryService(db, cashierService);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cashierInventoryService.DeleteProductAsync(product.Id));
        Assert.Contains("El usuario actual no tiene permisos para modificar el catálogo", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    // 8.6-B4: Ownership a nivel de objeto — la identidad del creador viaja DENTRO del objeto
    // persistido (ReferenceId de la reserva) y el guard de ReservationsController la valida
    // contra el claim NameIdentifier, no solo contra el rol.
    // ---------------------------------------------------------------------------------------

    private static ReservationsController CreateReservationsController(InventoryDbContext db, string userId, UserRole role)
    {
        var service = new InventoryService(db);
        var controller = new ReservationsController(service, db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId)
                }, "Test"))
            }
        };
        return controller;
    }

    private async Task<Product> SeedStockedProductAsync(InventoryDbContext db)
    {
        var service = new InventoryService(db);
        var product = new Product
        {
            Name = "Galletas",
            SKU = Guid.NewGuid().ToString("N")[..10],
            CostPriceUSD = 0.50m,
            PriceRetailUSD = 1.00m,
            StockQuantity = 50m
        };
        return await service.CreateProductAsync(product);
    }

    [Fact]
    public async Task Reservation_ObjectCarriesCreatingUserLineage()
    {
        using var db = GetInMemoryInventoryDbContext();
        var product = await SeedStockedProductAsync(db);
        var controller = CreateReservationsController(db, "user-42", UserRole.Cashier);

        var result = await controller.ReserveStock(new ReserveStockDto
        {
            ProductId = product.Id,
            Quantity = 3m,
            DurationSeconds = 300
        });

        Assert.IsType<OkObjectResult>(result);

        // El objeto persistido conserva la identidad del creador (ownership a nivel de objeto).
        var reservation = db.StockReservations.Single(r => r.ProductId == product.Id);
        Assert.Equal("user:user-42", reservation.ReferenceId);
        Assert.False(reservation.IsConfirmed);
        Assert.Equal(3m, reservation.Quantity);
        Assert.True(reservation.ExpiryDate > DateTime.UtcNow);
    }

    [Fact]
    public async Task Reservation_NonOwnerUser_CannotConfirmOrCancel()
    {
        using var db = GetInMemoryInventoryDbContext();
        var product = await SeedStockedProductAsync(db);

        var ownerController = CreateReservationsController(db, "user-42", UserRole.Cashier);
        var reserveResult = await ownerController.ReserveStock(new ReserveStockDto
        {
            ProductId = product.Id,
            Quantity = 3m,
            DurationSeconds = 300
        });
        var ok = Assert.IsType<OkObjectResult>(reserveResult);
        var reservationId = Convert.ToInt32(ok.Value!.GetType().GetProperty("ReservationId")!.GetValue(ok.Value));

        // Usuario distinto NO debe confirmar ni cancelar la reserva ajena.
        var intruderController = CreateReservationsController(db, "user-99", UserRole.Cashier);

        var confirmResult = await intruderController.ConfirmReservation(reservationId, new ConfirmReservationDto { Reason = "Retiro" });
        var forbidConfirm = Assert.IsType<ObjectResult>(confirmResult);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidConfirm.StatusCode);

        var cancelResult = await intruderController.CancelReservation(reservationId);
        var forbidCancel = Assert.IsType<ObjectResult>(cancelResult);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidCancel.StatusCode);

        // La reserva sigue viva tras el intento de un tercero (ownership preservado).
        Assert.True(db.StockReservations.Any(r => r.Id == reservationId && !r.IsConfirmed));

        // El propietario sí puede confirmarla: la reserva se consume por completo
        // (fila eliminada) y el stock del producto baja en la cantidad reservada.
        var ownerResult = await ownerController.ConfirmReservation(reservationId, new ConfirmReservationDto { Reason = "Retiro" });
        Assert.IsType<NoContentResult>(ownerResult);
        Assert.DoesNotContain(db.StockReservations, r => r.Id == reservationId);
        var productAfter = db.Products.Single(p => p.Id == product.Id);
        Assert.Equal(47m, productAfter.StockQuantity);
        Assert.Equal(0m, productAfter.ReservedQuantity);
    }
}
