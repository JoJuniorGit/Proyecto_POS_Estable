using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.DTOs;
using CommandCenter.Tests.Builders;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.177-T1 (S1): datos de auditoría del Kardex — clasificación (origen/motivo estandarizado),
/// autorizador, costo unitario histórico, referencia a factura proveedor y observaciones.
/// Cubre defaults centrales de venta, productores (carga inicial, importación, factura),
/// validaciones del ajuste manual, mapeo al archivo y contrato del controller.
/// </summary>
public sealed class StockMovementAuditTests
{
    private static Mock<ICurrentUserService> CreateAdminUserMock()
    {
        var mock = new Mock<ICurrentUserService>();
        mock.Setup(u => u.CanMutateCatalog).Returns(true);
        mock.Setup(u => u.UserId).Returns("admin-8177");
        return mock;
    }

    private static InventoryService CreateService(InventoryDbContext db) =>
        new(db, CreateAdminUserMock().Object);

    private static SupplierInvoiceService CreateBackendInvoiceService(InventoryDbContext db) =>
        new(
            db,
            Mock.Of<ISystemSettingsService>(),
            Mock.Of<ISupplierProductSimilaritySearch>(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IProductManagementService>());

    private static CreateProductDto CreateProduct(string sku, decimal costUsd, decimal stock) => new()
    {
        Name = $"Producto {sku}",
        SKU = sku,
        StockQuantity = stock,
        CostPriceUSD = costUsd,
        ProfitMarginRetail = 30m
    };

    [Fact]
    public async Task AdjustStockAsync_WithAuditFields_PersistsManualOriginObservationAndCostSnapshot()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(db);
        var created = await service.CreateProductFromDtoAsync(CreateProduct("8177-MAN-1", 12.5m, 10m));

        await service.AdjustStockAsync(
            created.Id,
            3m,
            "Conteo físico",
            reasonCode: StockMovementReason.PhysicalCountCorrection,
            observation: "  Conteo semanal ciego  ",
            authorizedByUserId: "sup-7");

        var movement = await db.StockMovements
            .AsNoTracking()
            .SingleAsync(m => m.ReasonCode == StockMovementReason.PhysicalCountCorrection);

        Assert.Equal(StockMovementOriginType.Manual, movement.OriginType);
        Assert.Equal("Conteo semanal ciego", movement.Observation);
        Assert.Equal("sup-7", movement.AuthorizedByUserId);
        Assert.Equal(12.5m, movement.UnitCostUSD);
        Assert.Equal(13m, movement.NewStockLevel);
        Assert.Equal(3m, movement.QuantityChange);
        Assert.StartsWith("Ajuste Manual: ", movement.Reason);
    }

    [Fact]
    public async Task AdjustStockAsync_LegacyCallWithoutAuditFields_LeavesClassificationUnset()
    {
        // 8.177-T1: el cliente Desktop actual no envía motivo/observación; el movimiento no debe
        // clasificarse a ciegas (los históricos/legados quedan nullable).
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(db);
        var created = await service.CreateProductFromDtoAsync(CreateProduct("8177-LEG-1", 5m, 10m));

        await service.AdjustStockAsync(created.Id, -2m, "Merma");

        var movement = await db.StockMovements
            .AsNoTracking()
            .SingleAsync(m => m.Reason == "Ajuste Manual: Merma");
        Assert.Null(movement.OriginType);
        Assert.Null(movement.ReasonCode);
        Assert.Null(movement.Observation);
        Assert.Null(movement.AuthorizedByUserId);
        Assert.Equal(5m, movement.UnitCostUSD);
    }

    [Fact]
    public async Task AdjustStockAsync_InvalidAuditFields_RejectsBeforeTouchingStock()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(db);
        var created = await service.CreateProductFromDtoAsync(CreateProduct("8177-VAL-1", 5m, 10m));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AdjustStockAsync(created.Id, 1m, "Conteo", observation: "   "));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AdjustStockAsync(created.Id, 1m, "Conteo", observation: new string('x', 501)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AdjustStockAsync(created.Id, 1m, "Conteo", reasonCode: (StockMovementReason)999));

        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal(10m, product.StockQuantity);
        Assert.Empty(db.StockMovements.Where(m => m.Reason.StartsWith("Ajuste Manual: Conteo")));
    }

    [Fact]
    public async Task UpdateStockBatchAsync_SaleWithSaleId_AppliesCentralSaleDefaultsAndCostSnapshot()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(db);
        var created = await service.CreateProductFromDtoAsync(CreateProduct("8177-SALE-1", 4.25m, 10m));

        await service.UpdateStockBatchAsync(new[]
        {
            new StockDeductionRequest(created.Id, -2m, "Sale #77", SaleId: 77)
        });

        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.SaleId == 77);
        Assert.Equal(StockMovementOriginType.System, movement.OriginType);
        Assert.Equal(StockMovementReason.Sale, movement.ReasonCode);
        Assert.Equal(4.25m, movement.UnitCostUSD);
        Assert.Equal(8m, movement.NewStockLevel);
    }

    [Fact]
    public async Task CreateProduct_WithInitialStock_ClassifiesInitialLoadWithCostSnapshot()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(db);

        var created = await service.CreateProductFromDtoAsync(CreateProduct("8177-INIT-1", 3.75m, 10m));

        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.ProductId == created.Id);
        Assert.Equal(StockMovementOriginType.System, movement.OriginType);
        Assert.Equal(StockMovementReason.InitialLoad, movement.ReasonCode);
        Assert.Equal(3.75m, movement.UnitCostUSD);
        Assert.Null(movement.SaleId);
    }

    [Fact]
    public async Task BulkImportProducts_NewProductWithStock_ClassifiesImportAdjustmentWithCostSnapshot()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(db);

        var (added, updated) = await service.BulkImportProductsAsync(new[]
        {
            new ProductImportDto
            {
                SKU = "8177-IMP-1",
                Name = "Importado 8.177",
                CostPriceUSD = 5m,
                ProfitMarginRetail = 60m,
                StockQuantity = 4m
            }
        }, overwriteMerge: false);

        Assert.Equal(1, added);
        Assert.Equal(0, updated);

        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.QuantityChange == 4m);
        Assert.Equal(StockMovementOriginType.Import, movement.OriginType);
        Assert.Equal(StockMovementReason.ImportAdjustment, movement.ReasonCode);
        Assert.Equal(5m, movement.UnitCostUSD);
        Assert.Null(movement.SupplierInvoiceId);
    }

    [Fact]
    public async Task ConfirmSupplierInvoice_MovementCarriesPurchaseEntryAuditAndInvoiceReference()
    {
        // Reutiliza el arnés de SupplierInvoiceClientTests: factura Bs.S con tasa ajustada en revisión.
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = new Supplier
        {
            RifOrNit = "J-8177",
            NormalizedRifOrNit = "J8177",
            CommercialName = "Proveedor 8.177",
            NormalizedCommercialName = "PROVEEDOR 8177"
        };
        var product = new ProductBuilder()
            .WithSku("8177-INV-1")
            .WithName("Café 8.177")
            .WithCostAndMargin(8m, 25m)
            .WithStock(5m)
            .Build();
        context.Suppliers.Add(supplier);
        context.Products.Add(product);
        await context.SaveChangesAsync();

        var invoiceService = CreateBackendInvoiceService(context);
        var staged = await invoiceService.StageAsync(new StageSupplierInvoiceRequestDto(
            supplier.Id,
            supplier.RifOrNit,
            supplier.CommercialName,
            null,
            new[] { new StageLineDto("SUP-1", product.SKU, product.Name, 2m, 800m) },
            CurrencyCodes.BsS,
            40m,
            OcrSourced: true));

        await invoiceService.ConfirmAsync(
            staged.Id,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(staged.Lines[0].Id, true, 25m, 25m) },
                AppliedRate: 80m));

        var movement = await context.StockMovements
            .AsNoTracking()
            .SingleAsync(m => m.SupplierInvoiceId == staged.Id);

        Assert.Equal(StockMovementOriginType.Import, movement.OriginType);
        Assert.Equal(StockMovementReason.PurchaseEntry, movement.ReasonCode);
        Assert.Equal(10m, movement.UnitCostUSD);
        Assert.Equal(product.Id, movement.ProductId);
        Assert.Equal(2m, movement.QuantityChange);
    }

    [Fact]
    public async Task UpdateStockBatchAsync_SharedStockVariant_SnapshotsParentCostOnTargetMovement()
    {
        // 8.177-T1 (fix verificación independiente): una variante con stock compartido mueve el
        // stock del PADRE; el snapshot de costo del movimiento debe ser el del producto DESTINO
        // (padre), nunca null — el dato histórico no es recuperable después.
        using var db = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateService(db);

        var parent = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Harina Saco 8.177",
            SKU = "8177-PAR-1",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 100m,
            CostPriceUSD = 2.5m,
            ProfitMarginRetail = 30m
        });
        var variant = await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = "Harina 5Kg 8.177",
            SKU = "8177-VAR-1",
            ParentProductId = parent.Id,
            ConversionFactor = 5m,
            CostPriceUSD = 0.9m,
            ProfitMarginRetail = 30m
        });

        await service.UpdateStockBatchAsync(new[]
        {
            new StockDeductionRequest(variant.Id, -3m, "Sale #8177", SaleId: 8177)
        });

        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.SaleId == 8177);
        Assert.Equal(parent.Id, movement.ProductId);
        Assert.Equal(-15m, movement.QuantityChange); // 3 unidades de variante × factor 5
        Assert.Equal(2.5m, movement.UnitCostUSD);    // costo del PADRE (producto destino)
        Assert.Equal(StockMovementReason.Sale, movement.ReasonCode);
        Assert.Equal(StockMovementOriginType.System, movement.OriginType);
    }

    [Fact]
    public void StockMovementArchive_FromStockMovement_MirrorsAuditFields()
    {
        var movementDate = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var movement = new StockMovement
        {
            Id = 42,
            ProductId = 9,
            QuantityChange = -3m,
            NewStockLevel = 7m,
            Reason = "Sale #1",
            SaleId = 1,
            MovementDate = movementDate,
            UserId = "cashier-1",
            OriginType = StockMovementOriginType.System,
            ReasonCode = StockMovementReason.Sale,
            AuthorizedByUserId = "sup-9",
            UnitCostUSD = 1.2345m,
            SupplierInvoiceId = 55,
            Observation = "obs-8177"
        };

        var archived = StockMovementArchive.FromStockMovement(movement);

        Assert.Equal(42, archived.OriginalMovementId);
        Assert.Equal(9, archived.ProductId);
        Assert.Equal(-3m, archived.QuantityChange);
        Assert.Equal(7m, archived.NewStockLevel);
        Assert.Equal("Sale #1", archived.Reason);
        Assert.Equal(1, archived.SaleId);
        Assert.Equal(movementDate, archived.MovementDate);
        Assert.Equal("cashier-1", archived.UserId);
        Assert.Equal(StockMovementOriginType.System, archived.OriginType);
        Assert.Equal(StockMovementReason.Sale, archived.ReasonCode);
        Assert.Equal("sup-9", archived.AuthorizedByUserId);
        Assert.Equal(1.2345m, archived.UnitCostUSD);
        Assert.Equal(55, archived.SupplierInvoiceId);
        Assert.Equal("obs-8177", archived.Observation);
        Assert.True(archived.ArchivedAtUtc >= DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ProductsController_AdjustStock_InvalidAuditFields_Returns400()
    {
        var mockService = new Mock<IInventoryService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);
        var controller = new ProductsController(mockService.Object, Mock.Of<IProductManagementService>(), mockUser.Object);

        var tooLong = await controller.AdjustStockAsync(1, new AdjustStockDto
        {
            QuantityChange = 1m,
            Reason = "Conteo",
            Observation = new string('x', 501)
        });
        var tooLongResult = Assert.IsAssignableFrom<ObjectResult>(tooLong);
        Assert.Equal(StatusCodes.Status400BadRequest, tooLongResult.StatusCode);

        var undefinedEnum = await controller.AdjustStockAsync(1, new AdjustStockDto
        {
            QuantityChange = 1m,
            Reason = "Conteo",
            ReasonCode = (StockMovementReason)999
        });
        var undefinedResult = Assert.IsAssignableFrom<ObjectResult>(undefinedEnum);
        Assert.Equal(StatusCodes.Status400BadRequest, undefinedResult.StatusCode);

        mockService.Verify(s => s.AdjustStockAsync(
            It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<StockMovementReason?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProductsController_AdjustStock_ValidAuditFields_ForwardedToService()
    {
        var mockService = new Mock<IInventoryService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);
        var controller = new ProductsController(mockService.Object, Mock.Of<IProductManagementService>(), mockUser.Object);

        mockService
            .Setup(s => s.AdjustStockAsync(
                1, 1m, "Conteo", null, StockMovementReason.Damage, "Empaque roto", null,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await controller.AdjustStockAsync(1, new AdjustStockDto
        {
            QuantityChange = 1m,
            Reason = "Conteo",
            ReasonCode = StockMovementReason.Damage,
            Observation = "Empaque roto"
        });

        Assert.IsType<NoContentResult>(result);
        mockService.Verify(s => s.AdjustStockAsync(
            1, 1m, "Conteo", null, StockMovementReason.Damage, "Empaque roto", null,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
