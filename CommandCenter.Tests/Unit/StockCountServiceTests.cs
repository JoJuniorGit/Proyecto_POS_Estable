using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Attributes;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.180-T1 (S1-S4): backend del Paso 6 — conteo físico ciego. Cubre alta/listado, detalle CIEGO
/// (sin teórico ni costo), scan/set/remove con guards, alcance por subárbol de categoría, review
/// (preview LIVE vs congelado), cierre, apply atómico con trazabilidad completa en movimientos,
/// cancelación y contrato RBAC del controller.
/// </summary>
public sealed class StockCountServiceTests
{
    private const string CounterUserId = "counter-8180";

    // ---------------------------------------------------------------------------------------------
    // Alta / listado / detalle ciego
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PersistsOpenSessionWithScopeCreatorAndTrimmedTexts()
    {
        var (service, context, _) = CreateService();
        var parent = await AddCategoryAsync(context, "Bebidas", null);
        await AddCategoryAsync(context, "Gaseosas", parent.Id);

        var created = await service.CreateAsync(new CreateStockCountRequestDto("  Conteo Octubre  ", parent.Id, "  notas del conteo  "));

        Assert.True(created.Id > 0);
        Assert.Equal("Conteo Octubre", created.Name);
        Assert.Equal(StockCountStatus.Open, created.Status);
        Assert.Equal(parent.Id, created.ScopeCategoryId);
        Assert.Equal("Bebidas", created.ScopeCategoryPath);
        Assert.Equal(0, created.ItemsCount);
        Assert.Equal(CounterUserId, created.CreatedByUserId);
        Assert.Null(created.ClosedAt);
        Assert.Null(created.AppliedAt);

        var persisted = await context.StockCountSessions.AsNoTracking().SingleAsync();
        Assert.Equal("notas del conteo", persisted.Notes);
        Assert.Equal(StockCountStatus.Open, persisted.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_EmptyName_ThrowsArgumentException(string name)
    {
        var (service, context, _) = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CreateStockCountRequestDto(name)));
        Assert.Empty(await context.StockCountSessions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_OverlongNameOrNotes_ThrowsArgumentException_AndMissingCategoryThrowsKeyNotFound()
    {
        var (service, context, _) = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CreateStockCountRequestDto(new string('A', 81))));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CreateStockCountRequestDto("Conteo", null, new string('B', 301))));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateAsync(new CreateStockCountRequestDto("Conteo", 999)));
        Assert.Empty(await context.StockCountSessions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ListAsync_FiltersByStatusAndReturnsItemCounts()
    {
        var (service, context, _) = CreateService();
        var first = await service.CreateAsync(new CreateStockCountRequestDto("Sesión A"));
        await service.CreateAsync(new CreateStockCountRequestDto("Sesión B"));
        await CloseAsync(service, first.Id);

        var all = await service.ListAsync();
        var closed = await service.ListAsync(StockCountStatus.Closed);
        var open = await service.ListAsync(StockCountStatus.Open);

        Assert.Equal(2, all.Count);
        Assert.Single(closed);
        Assert.Equal(first.Id, closed[0].Id);
        Assert.Single(open);
        Assert.Equal("Sesión B", open[0].Name);
    }

    [Fact]
    public void StockCountItemDto_IsBlind_NoTheoreticalOrCostContract()
    {
        // 8.180-T1 (S3): el contrato ciego es por TIPO: el ítem del detalle/scan no puede exponer
        // teórico, costo ni valor (solo el review los sirve a Admin/Manager).
        var forbiddenFragments = new[] { "theoretical", "cost", "value", "difference" };
        var exposed = typeof(StockCountItemDto).GetProperties()
            .Select(p => p.Name)
            .Where(name => forbiddenFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(exposed);

        // El detalle ciego solo expone la sesión + ítems; la sesión tampoco filtra teórico/costo.
        var detailProperties = typeof(StockCountDetailDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Session", "Items" }, detailProperties);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsBlindItemsOrderedByProductName()
    {
        var (service, context, _) = CreateService();
        var zebra = await AddProductAsync(context, new ProductBuilder().WithSku("8180-Z").WithName("Zapatos").WithStock(5m));
        var abrigo = await AddProductAsync(context, new ProductBuilder().WithSku("8180-A").WithName("Abrigos").WithStock(5m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-Z"));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-A", 2m));

        var detail = await service.GetDetailAsync(session.Id);

        Assert.Equal(session.Id, detail.Session.Id);
        Assert.Equal(2, detail.Session.ItemsCount);
        Assert.Equal(new[] { abrigo.Id, zebra.Id }, detail.Items.Select(i => i.ProductId).ToArray());
        Assert.Equal(new[] { "Abrigos", "Zapatos" }, detail.Items.Select(i => i.ProductName).ToArray());
        Assert.Equal(2m, detail.Items[0].CountedQuantity);
        Assert.Equal(1m, detail.Items[1].CountedQuantity);
        Assert.NotEqual(default, detail.Items[0].UpdatedAt);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetDetailAsync(999));
    }

    // ---------------------------------------------------------------------------------------------
    // Scan
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ScanAsync_KnownSku_TrimsCodeAndAccumulatesIntoOneUpsertedItem()
    {
        var (service, context, _) = CreateService();
        var product = await AddProductAsync(context, new ProductBuilder().WithSku("7591234567890").WithName("Harina").WithStock(100m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));

        var first = await service.ScanAsync(session.Id, new ScanStockCountRequestDto("  7591234567890  "));
        var second = await service.ScanAsync(session.Id, new ScanStockCountRequestDto("7591234567890"));
        var third = await service.ScanAsync(session.Id, new ScanStockCountRequestDto("7591234567890", 3m));

        Assert.Equal(product.Id, first.ProductId);
        Assert.Equal("7591234567890", first.SKU);
        Assert.Equal("Harina", first.ProductName);
        Assert.Equal(1m, first.CountedQuantity);
        Assert.Equal(2m, second.CountedQuantity);
        Assert.Equal(5m, third.CountedQuantity);

        var items = await context.StockCountItems.AsNoTracking().ToListAsync();
        Assert.Single(items);
        Assert.Equal(5m, items[0].CountedQuantity);
    }

    [Fact]
    public async Task ScanAsync_UnknownCode_ThrowsKeyNotFound()
    {
        var (service, context, _) = CreateService();
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.ScanAsync(session.Id, new ScanStockCountRequestDto("NO-EXISTE")));

        Assert.Contains("NO-EXISTE", exception.Message);
        Assert.Empty(await context.StockCountItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ScanAsync_NonOpenSession_Throws409()
    {
        var (service, context, _) = CreateService();
        await AddProductAsync(context, new ProductBuilder().WithSku("8180-OPEN").WithStock(1m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));
        await CloseAsync(service, session.Id);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-OPEN")));

        Assert.Contains("no está abierta", exception.Message);
    }

    [Fact]
    public async Task ScanAsync_DeletedProduct_Throws409()
    {
        var (service, context, _) = CreateService();
        var product = await AddProductAsync(context, new ProductBuilder().WithSku("8180-DEL").WithStock(1m));
        product.IsDeleted = true;
        await context.SaveChangesAsync();
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-DEL")));
        Assert.Empty(await context.StockCountItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ScanAsync_CashAdvanceOrGroupIndividualOrSharedVariant_Throws409()
    {
        var (service, context, _) = CreateService();
        var cashAdvance = new Product { SKU = "8180-CASH", Name = "Adelanto", StockQuantity = 0m, IsCashAdvance = true };
        var groupIndividual = new Product { SKU = "8180-GRP", Name = "Grupo Individual", StockQuantity = 10m, IsGroupHeader = true, IsStockShared = false };
        var parent = new Product { SKU = "8180-PAR", Name = "Padre Compartido", StockQuantity = 20m, IsGroupHeader = true, IsStockShared = true };
        context.Products.AddRange(cashAdvance, groupIndividual, parent);
        await context.SaveChangesAsync();
        var variant = new Product { SKU = "8180-VAR", Name = "Variante", StockQuantity = 0m, ParentProductId = parent.Id, ConversionFactor = 5m };
        context.Products.Add(variant);
        await context.SaveChangesAsync();
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-CASH")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-GRP")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-VAR")));
        Assert.Empty(await context.StockCountItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ScanAsync_ProductOutsideScopeCategory_Throws409()
    {
        var (service, context, _) = CreateService();
        var scope = await AddCategoryAsync(context, "Alcance", null);
        var other = await AddCategoryAsync(context, "Otra", null);
        var outside = await AddProductAsync(context, new ProductBuilder().WithSku("8180-OUT").WithStock(1m));
        context.ProductCategories.Add(new ProductCategory { ProductId = outside.Id, CategoryId = other.Id });
        await context.SaveChangesAsync();
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo", scope.Id));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-OUT")));

        Assert.Contains("alcance", exception.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Set / Remove / alcance por subárbol
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetAndRemove_WorkWithinOpenSession_AndSubtreeCategoryScope()
    {
        var (service, context, _) = CreateService();
        var parent = await AddCategoryAsync(context, "Almacén", null);
        var child = await AddCategoryAsync(context, "Lácteos", parent.Id);
        var product = await AddProductAsync(context, new ProductBuilder().WithSku("8180-LAC").WithStock(10m));
        context.ProductCategories.Add(new ProductCategory { ProductId = product.Id, CategoryId = child.Id });
        await context.SaveChangesAsync();

        // El alcance del padre incluye el subárbol: la hija es contable.
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo", parent.Id));
        var scanned = await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-LAC"));
        Assert.Equal(1m, scanned.CountedQuantity);

        var set = await service.SetCountedQuantityAsync(session.Id, product.Id, new SetCountedQuantityRequestDto(7.5m));
        Assert.Equal(7.5m, set.CountedQuantity);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetCountedQuantityAsync(session.Id, product.Id, new SetCountedQuantityRequestDto(-1m)));

        await service.RemoveItemAsync(session.Id, product.Id);
        Assert.Empty(await context.StockCountItems.AsNoTracking().ToListAsync());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RemoveItemAsync(session.Id, product.Id));
    }

    [Fact]
    public async Task SetCountedQuantityAsync_ClosedSession_Throws409()
    {
        var (service, context, _) = CreateService();
        var product = await AddProductAsync(context, new ProductBuilder().WithSku("8180-SET").WithStock(3m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));
        await CloseAsync(service, session.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetCountedQuantityAsync(session.Id, product.Id, new SetCountedQuantityRequestDto(1m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveItemAsync(session.Id, product.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Review: preview vs congelado, totales y pendientes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReviewAsync_OpenSession_UsesLiveStockAsPreviewAndComputesShortage()
    {
        var (service, context, _) = CreateService();
        await AddProductAsync(context, new ProductBuilder().WithSku("8180-REV").WithName("Revisión").WithCostAndMargin(2.50m, 30m).WithStock(10m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-REV", 8m));

        var review = await service.ReviewAsync(session.Id);

        Assert.True(review.TheoreticalIsPreview);
        Assert.Equal(StockCountStatus.Open, review.Status);
        var row = Assert.Single(review.Items);
        Assert.Equal(10m, row.TheoreticalStock);
        Assert.Equal(8m, row.CountedQuantity);
        Assert.Equal(-2m, row.Difference);
        Assert.Equal(2.50m, row.UnitCostUSD);
        Assert.Equal(-5.00m, row.DifferenceValueUsd);
        Assert.True(row.TheoreticalIsPreview);
        Assert.Equal(0m, review.TotalSurplusQuantity);
        Assert.Equal(2m, review.TotalShortageQuantity);
        Assert.Equal(0m, review.TotalSurplusValueUsd);
        Assert.Equal(5.00m, review.TotalShortageValueUsd);
        Assert.Equal(-5.00m, review.NetValueUsd);
        Assert.Equal(0, review.UnscannedInScopeCount);
    }

    [Fact]
    public async Task ReviewAsync_ClosedSession_UsesFrozenTheoreticalAndCostWithFiscalRounding()
    {
        var (service, context, _) = CreateService();
        var surplus = await AddProductAsync(context, new ProductBuilder().WithSku("8180-SUR").WithName("A Sobrante").WithCostAndMargin(2m, 30m).WithStock(10m));
        var shortage = await AddProductAsync(context, new ProductBuilder().WithSku("8180-SHO").WithName("B Faltante").WithCostAndMargin(0.125m, 30m).WithStock(5m));
        var unscanned = await AddProductAsync(context, new ProductBuilder().WithSku("8180-UNS").WithName("C No escaneado").WithStock(50m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-SUR", 12m));   // Δ +2 → +4.00
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-SHO", 2m));    // Δ -3 → -0.375 → -0.38
        await CloseAsync(service, session.Id);

        // Cambios posteriores al cierre NO deben alterar el descuadre (snapshots).
        surplus.StockQuantity = 999m;
        surplus.CostPriceUSD = 99m;
        await context.SaveChangesAsync();

        var review = await service.ReviewAsync(session.Id);

        Assert.False(review.TheoreticalIsPreview);
        Assert.Equal(2, review.Items.Count);
        Assert.DoesNotContain(review.Items, i => i.ProductId == unscanned.Id);

        var surplusRow = review.Items.Single(i => i.ProductId == surplus.Id);
        Assert.Equal(10m, surplusRow.TheoreticalStock);
        Assert.Equal(2m, surplusRow.UnitCostUSD);
        Assert.Equal(2m, surplusRow.Difference);
        Assert.Equal(4.00m, surplusRow.DifferenceValueUsd);

        var shortageRow = review.Items.Single(i => i.ProductId == shortage.Id);
        Assert.Equal(5m, shortageRow.TheoreticalStock);
        Assert.Equal(0.125m, shortageRow.UnitCostUSD);
        Assert.Equal(-3m, shortageRow.Difference);
        Assert.Equal(-0.38m, shortageRow.DifferenceValueUsd); // 0.375 → AwayFromZero

        Assert.Equal(2m, review.TotalSurplusQuantity);
        Assert.Equal(3m, review.TotalShortageQuantity);
        Assert.Equal(4.00m, review.TotalSurplusValueUsd);
        Assert.Equal(0.38m, review.TotalShortageValueUsd);
        Assert.Equal(3.62m, review.NetValueUsd);
        Assert.Equal(1, review.UnscannedInScopeCount); // "C No escaneado" queda pendiente
    }

    // ---------------------------------------------------------------------------------------------
    // Close
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_FreezesTheoreticalAndCost_AndRejectsSecondClose()
    {
        var (service, context, _) = CreateService();
        var product = await AddProductAsync(context, new ProductBuilder().WithSku("8180-CLO").WithCostAndMargin(3m, 30m).WithStock(9m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-CLO", 9m));

        var closed = await service.CloseAsync(session.Id);

        Assert.Equal(StockCountStatus.Closed, closed.Status);
        Assert.NotNull(closed.ClosedAt);
        var item = await context.StockCountItems.AsNoTracking().SingleAsync();
        Assert.Equal(9m, item.TheoreticalStock);
        Assert.Equal(3m, item.UnitCostUSD);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloseAsync(session.Id));
        Assert.Contains("no está abierta", exception.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Apply
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ApplyAsync_CreatesAuditedMovementAndLeavesStockExactlyAtCounted()
    {
        var (service, context, _) = CreateService();
        var product = await AddProductAsync(context, new ProductBuilder().WithSku("8180-APP").WithName("Aplicado").WithCostAndMargin(2.50m, 30m).WithStock(10m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo Semanal"));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-APP", 12m));
        await CloseAsync(service, session.Id);

        var applied = await service.ApplyAsync(session.Id);

        Assert.Equal(StockCountStatus.Applied, applied.Status);
        Assert.NotNull(applied.AppliedAt);
        var savedSession = await context.StockCountSessions.AsNoTracking().SingleAsync();
        Assert.Equal(CounterUserId, savedSession.AppliedByUserId);

        var stock = await context.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id);
        Assert.Equal(12m, stock.StockQuantity);

        var movement = await context.StockMovements.AsNoTracking().SingleAsync();
        Assert.Equal(product.Id, movement.ProductId);
        Assert.Equal(2m, movement.QuantityChange);
        Assert.Equal(12m, movement.NewStockLevel);
        Assert.Equal(StockMovementOriginType.Manual, movement.OriginType);
        Assert.Equal(StockMovementReason.PhysicalCountCorrection, movement.ReasonCode);
        Assert.Equal($"Toma física #C-{session.Id} Conteo Semanal", movement.Observation);
        Assert.Equal(session.Id, movement.StockCountSessionId);
        Assert.Equal(CounterUserId, movement.AuthorizedByUserId);
        Assert.Equal(2.50m, movement.UnitCostUSD);
    }

    [Fact]
    public async Task ApplyAsync_AfterPostCloseSale_UsesDeltaAgainstCurrentStock()
    {
        var (service, context, inventoryService, _) = CreateServiceWithInventory();
        var product = await AddProductAsync(context, new ProductBuilder().WithSku("8180-SALE").WithCostAndMargin(1m, 30m).WithStock(10m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-SALE", 12m));
        await CloseAsync(service, session.Id);

        // Venta posterior al cierre: el Δ del apply se calcula contra el stock ACTUAL, por lo que el
        // stock final queda EXACTAMENTE en lo contado (12), no en 12−(−3).
        await inventoryService.UpdateStockBatchAsync(new[]
        {
            new StockDeductionRequest(product.Id, -3m, "Sale #1", SaleId: 1)
        });
        Assert.Equal(7m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id)).StockQuantity);

        await service.ApplyAsync(session.Id);

        Assert.Equal(12m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id)).StockQuantity);
        var countMovement = await context.StockMovements.AsNoTracking().SingleAsync(m => m.StockCountSessionId == session.Id);
        Assert.Equal(5m, countMovement.QuantityChange);
        Assert.Equal(12m, countMovement.NewStockLevel);
    }

    [Fact]
    public async Task ApplyAsync_ZeroDeltaAndUnscannedProducts_ProduceNoMovementAndStayUntouched()
    {
        var (service, context, _) = CreateService();
        var exact = await AddProductAsync(context, new ProductBuilder().WithSku("8180-EQX").WithStock(7m));
        var untouched = await AddProductAsync(context, new ProductBuilder().WithSku("8180-UNT").WithStock(4m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-EQX", 7m));
        await CloseAsync(service, session.Id);

        await service.ApplyAsync(session.Id);

        Assert.Empty(await context.StockMovements.AsNoTracking().ToListAsync());
        Assert.Equal(7m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == exact.Id)).StockQuantity);
        Assert.Equal(4m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == untouched.Id)).StockQuantity);
        Assert.Equal(StockCountStatus.Applied, (await context.StockCountSessions.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ApplyAsync_RequiresClosedStatus_AndRejectsReapply()
    {
        var (service, context, _) = CreateService();
        var product = await AddProductAsync(context, new ProductBuilder().WithSku("8180-RPT").WithStock(5m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-RPT", 6m));

        var openApply = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(session.Id));
        Assert.Contains("cerrada", openApply.Message);

        await CloseAsync(service, session.Id);
        await service.ApplyAsync(session.Id);

        var reapply = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(session.Id));
        Assert.Contains("ya fue aplicada", reapply.Message);
        Assert.Equal(6m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id)).StockQuantity);
    }

    [Fact]
    public async Task ApplyAsync_ProductDeletedAfterClose_Throws409WithoutStockOrStatusChanges()
    {
        var (service, context, _) = CreateService();
        var product = await AddProductAsync(context, new ProductBuilder().WithSku("8180-DEL2").WithStock(5m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("8180-DEL2", 8m));
        await CloseAsync(service, session.Id);

        product.IsDeleted = true;
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(session.Id));

        Assert.Contains("eliminado", exception.Message);
        Assert.Equal(5m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id)).StockQuantity);
        Assert.Empty(await context.StockMovements.AsNoTracking().ToListAsync());
        Assert.Equal(StockCountStatus.Closed, (await context.StockCountSessions.AsNoTracking().SingleAsync()).Status);
    }

    // ---------------------------------------------------------------------------------------------
    // Cancel
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CancelAsync_FromClosedSucceeds_FromAppliedIsRejected()
    {
        var (service, context, _) = CreateService();
        await AddProductAsync(context, new ProductBuilder().WithSku("8180-CAN").WithStock(3m));
        var cancelled = await service.CreateAsync(new CreateStockCountRequestDto("Cancelable"));
        await service.ScanAsync(cancelled.Id, new ScanStockCountRequestDto("8180-CAN", 3m));
        await CloseAsync(service, cancelled.Id);

        var result = await service.CancelAsync(cancelled.Id);
        Assert.Equal(StockCountStatus.Cancelled, result.Status);

        var applied = await service.CreateAsync(new CreateStockCountRequestDto("Aplicada"));
        await service.ScanAsync(applied.Id, new ScanStockCountRequestDto("8180-CAN", 5m));
        await CloseAsync(service, applied.Id);
        await service.ApplyAsync(applied.Id);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(applied.Id));
        Assert.Contains("no puede cancelarse", exception.Message);
    }

    [Fact]
    public async Task CancelAsync_FromOpenSucceeds()
    {
        var (service, context, _) = CreateService();
        var session = await service.CreateAsync(new CreateStockCountRequestDto("Conteo"));

        var cancelled = await service.CancelAsync(session.Id);

        Assert.Equal(StockCountStatus.Cancelled, cancelled.Status);
        Assert.Equal(StockCountStatus.Cancelled, (await context.StockCountSessions.AsNoTracking().SingleAsync()).Status);
    }

    // ---------------------------------------------------------------------------------------------
    // Contrato RBAC del controller
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Controller_IsRestrictedToAdminManagerCashier_WithSecurityStampValidation()
    {
        var controllerType = typeof(StockCountsController);

        var authorize = (AuthorizeAttribute?)Attribute.GetCustomAttribute(controllerType, typeof(AuthorizeAttribute));
        Assert.NotNull(authorize);
        Assert.Equal("Admin,Manager,Cashier", authorize!.Roles);

        Assert.NotNull(Attribute.GetCustomAttribute(controllerType, typeof(RequireSecurityStampValidationAttribute)));

        var route = (RouteAttribute?)Attribute.GetCustomAttribute(controllerType, typeof(RouteAttribute));
        Assert.NotNull(route);
        Assert.Equal("api/stock-counts", route!.Template);
    }

    [Theory]
    [InlineData(nameof(StockCountsController.CreateAsync), "Admin,Manager")]
    [InlineData(nameof(StockCountsController.ReviewAsync), "Admin,Manager")]
    [InlineData(nameof(StockCountsController.CloseAsync), "Admin,Manager")]
    [InlineData(nameof(StockCountsController.ApplyAsync), "Admin,Manager")]
    [InlineData(nameof(StockCountsController.CancelAsync), "Admin,Manager")]
    public void Controller_SessionMutationsAndReview_AreAdminManagerOnly(string methodName, string expectedRoles)
    {
        var method = typeof(StockCountsController).GetMethod(methodName)!;
        var authorize = (AuthorizeAttribute?)Attribute.GetCustomAttribute(method, typeof(AuthorizeAttribute));

        Assert.NotNull(authorize);
        Assert.Equal(expectedRoles, authorize!.Roles);
    }

    [Theory]
    [InlineData(nameof(StockCountsController.GetAllAsync))]
    [InlineData(nameof(StockCountsController.GetByIdAsync))]
    [InlineData(nameof(StockCountsController.ScanAsync))]
    [InlineData(nameof(StockCountsController.SetCountedQuantityAsync))]
    [InlineData(nameof(StockCountsController.RemoveItemAsync))]
    public void Controller_CountingEndpoints_InheritClassRolesWithoutMethodOverride(string methodName)
    {
        var method = typeof(StockCountsController).GetMethod(methodName)!;
        Assert.Null(Attribute.GetCustomAttribute(method, typeof(AuthorizeAttribute)));
    }

    [Fact]
    public void Controller_Routes_MatchStockCountContract()
    {
        AssertRoute(nameof(StockCountsController.ScanAsync), typeof(HttpPostAttribute), "{id:int}/scan");
        AssertRoute(nameof(StockCountsController.SetCountedQuantityAsync), typeof(HttpPutAttribute), "{id:int}/items/{productId:int}");
        AssertRoute(nameof(StockCountsController.RemoveItemAsync), typeof(HttpDeleteAttribute), "{id:int}/items/{productId:int}");
        AssertRoute(nameof(StockCountsController.ReviewAsync), typeof(HttpGetAttribute), "{id:int}/review");
        AssertRoute(nameof(StockCountsController.CloseAsync), typeof(HttpPostAttribute), "{id:int}/close");
        AssertRoute(nameof(StockCountsController.ApplyAsync), typeof(HttpPostAttribute), "{id:int}/apply");
        AssertRoute(nameof(StockCountsController.CancelAsync), typeof(HttpPostAttribute), "{id:int}/cancel");
    }

    [Fact]
    public async Task Controller_CreateWithoutCatalogPermission_Returns403_AndDoesNotCallService()
    {
        var service = new Mock<IStockCountService>();
        var user = new Mock<ICurrentUserService>();
        user.SetupGet(u => u.CanMutateCatalog).Returns(false);
        var controller = new StockCountsController(service.Object, user.Object);

        var result = await controller.CreateAsync(new CreateStockCountRequestDto("Conteo"));

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        service.Verify(
            s => s.CreateAsync(It.IsAny<CreateStockCountRequestDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Controller_Create_Returns201CreatedAtAction()
    {
        var created = new StockCountSessionDto(4, "Conteo", StockCountStatus.Open, null, null, 0, CounterUserId, DateTime.UtcNow, null, null);
        var service = new Mock<IStockCountService>();
        service.Setup(s => s.CreateAsync(It.IsAny<CreateStockCountRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        var user = new Mock<ICurrentUserService>();
        user.SetupGet(u => u.CanMutateCatalog).Returns(true);
        var controller = new StockCountsController(service.Object, user.Object);

        var result = await controller.CreateAsync(new CreateStockCountRequestDto("Conteo"));

        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, createdResult.StatusCode);
        Assert.Same(created, createdResult.Value);
        Assert.Equal(nameof(StockCountsController.GetByIdAsync), createdResult.ActionName);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static void AssertRoute(string methodName, Type attributeType, string expectedTemplate)
    {
        var method = typeof(StockCountsController).GetMethod(methodName)!;
        var attribute = method.GetCustomAttributes(attributeType, inherit: false).Single();
        var template = attribute switch
        {
            HttpPostAttribute post => post.Template,
            HttpPutAttribute put => put.Template,
            HttpDeleteAttribute delete => delete.Template,
            HttpGetAttribute get => get.Template,
            _ => throw new InvalidOperationException($"Atributo HTTP no soportado: {attributeType.Name}")
        };
        Assert.Equal(expectedTemplate, template);
    }

    private static (StockCountService Service, InventoryDbContext Context, Mock<ICurrentUserService> User) CreateService(bool canMutateCatalog = true)
    {
        var (service, context, _, user) = CreateServiceWithInventory(canMutateCatalog);
        return (service, context, user);
    }

    private static (StockCountService Service, InventoryDbContext Context, InventoryService InventoryService, Mock<ICurrentUserService> User) CreateServiceWithInventory(bool canMutateCatalog = true)
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var user = new Mock<ICurrentUserService>();
        user.SetupGet(u => u.UserId).Returns(CounterUserId);
        user.SetupGet(u => u.CanMutateCatalog).Returns(canMutateCatalog);
        var inventoryService = new InventoryService(context, user.Object);
        return (new StockCountService(context, inventoryService, user.Object), context, inventoryService, user);
    }

    private static async Task<Product> AddProductAsync(InventoryDbContext context, ProductBuilder builder)
    {
        var product = builder.WithId(0).Build();
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product;
    }

    private static async Task<Category> AddCategoryAsync(InventoryDbContext context, string name, int? parentId)
    {
        var category = new Category { Name = name, NormalizedName = name.ToUpperInvariant(), ParentCategoryId = parentId };
        context.Categories.Add(category);
        await context.SaveChangesAsync();
        return category;
    }

    private static Task<StockCountSessionDto> CloseAsync(StockCountService service, int sessionId)
    {
        return service.CloseAsync(sessionId);
    }
}
