using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests;

// 8.29-A05: abonos atómicos por lote. Un lote se valida ANTES de persistir: si CUALQUIER
// abono falla (monto, límite acumulado, efectivo fraccionado, lote vacío o > 50), NINGUNO
// se persiste (todo o nada), porque todos los abonos comparten una única transacción.
public class BatchPaymentTests
{
    private const int TestActorId = 42;
    private readonly string _salesDbName = Guid.NewGuid().ToString();
    private readonly string _inventoryDbName = Guid.NewGuid().ToString();

    private SalesDbContext NewSalesDb() =>
        new(new DbContextOptionsBuilder<SalesDbContext>().UseInMemoryDatabase(_salesDbName).Options);

    private InventoryDbContext NewInventoryDb() =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(_inventoryDbName).Options);

    private async Task<SalesService> CreateServiceWithHeldSaleAsync(decimal totalUSD)
    {
        var salesDb = NewSalesDb();
        var invDb = NewInventoryDb();
        invDb.Products.Add(new Product
        {
            Id = 20,
            Name = "Aceite",
            SKU = "100020",
            PriceRetailUSD = totalUSD,
            StockQuantity = 50m
        });
        await invDb.SaveChangesAsync();

        salesDb.Customers.Add(new Customer { Id = 1, Name = "Consumidor Final", CedulaOrRif = "V-00000000", IsDefault = true });
        salesDb.Customers.Add(new Customer { Id = 5, Name = "Juan Perez", CedulaOrRif = "V-11223344", IsDefault = false });
        await salesDb.SaveChangesAsync();

        var service = new SalesService(salesDb, new InventoryService(invDb), null!, new CashDrawerService(salesDb), null!, null);
        var sale = await service.StartSaleAsync(1);
        await service.AddItemAsync(sale.Id, 20, 1m, 50m);
        await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto { CustomerId = 5, ExchangeRate = 50m });

        var heldSale = await salesDb.Sales.FindAsync(sale.Id);
        heldSale!.ClaimedByUserId = TestActorId;
        heldSale.ClaimAction = SaleClaimAction.Editing;
        heldSale.ClaimedByUserName = "Test Actor";
        heldSale.ClaimedAtUtc = DateTime.UtcNow;
        await salesDb.SaveChangesAsync();

        return service;
    }

    [Fact]
    public async Task AddPaymentsBatchToHoldSale_TodosValidosPersisteTodosLosAbonos()
    {
        using var salesDb = NewSalesDb();
        var service = await CreateServiceWithHeldSaleAsync(10m);
        var held = await salesDb.Sales.Include(s => s.Payments).OrderByDescending(s => s.Id).LastAsync();

        var result = await service.AddPaymentsBatchToHoldSaleAsync(held.Id, new List<AddPaymentRequestDto>
        {
            new() { PaymentMethodId = 1, AmountUSD = 6m, AmountBsS = 300m, ExchangeRate = 50m },
            new() { PaymentMethodId = 1, AmountUSD = 4m, AmountBsS = 200m, ExchangeRate = 50m }
        }, actingUserId: TestActorId);

        Assert.Equal(10m, result.TotalPaidUSD);
        Assert.Equal(0m, result.RemainingBalanceUSD);
        Assert.Equal(2, result.Payments.Count);

        using var fresh = NewSalesDb();
        var reloaded = fresh.Sales.Include(s => s.Payments).First(s => s.Id == held.Id);
        Assert.Equal(2, reloaded.Payments.Count);
        Assert.Equal(10m, reloaded.Payments.Sum(p => p.Amount));
    }

    [Fact]
    public async Task AddPaymentsBatchToHoldSale_UnAbonoExcedeTotalNoPersisteNinguno()
    {
        using var salesDb = NewSalesDb();
        var service = await CreateServiceWithHeldSaleAsync(10m);
        var held = await salesDb.Sales.Include(s => s.Payments).OrderByDescending(s => s.Id).LastAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddPaymentsBatchToHoldSaleAsync(held.Id, new List<AddPaymentRequestDto>
            {
                new() { PaymentMethodId = 1, AmountUSD = 6m, AmountBsS = 300m, ExchangeRate = 50m },
                new() { PaymentMethodId = 1, AmountUSD = 15m, AmountBsS = 750m, ExchangeRate = 50m }
            }, actingUserId: TestActorId));

        using var fresh = NewSalesDb();
        var reloaded = fresh.Sales.Include(s => s.Payments).First(s => s.Id == held.Id);
        Assert.Empty(reloaded.Payments);
    }

    [Fact]
    public async Task AddPaymentsBatchToHoldSale_SumaLoteExcedeTotalEnAcumuladoNoPersisteNinguno()
    {
        using var salesDb = NewSalesDb();
        var service = await CreateServiceWithHeldSaleAsync(10m);
        var held = await salesDb.Sales.Include(s => s.Payments).OrderByDescending(s => s.Id).LastAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddPaymentsBatchToHoldSaleAsync(held.Id, new List<AddPaymentRequestDto>
            {
                new() { PaymentMethodId = 1, AmountUSD = 4m, AmountBsS = 200m, ExchangeRate = 50m },
                new() { PaymentMethodId = 1, AmountUSD = 4m, AmountBsS = 200m, ExchangeRate = 50m },
                new() { PaymentMethodId = 1, AmountUSD = 4m, AmountBsS = 200m, ExchangeRate = 50m }
            }, actingUserId: TestActorId));

        using var fresh = NewSalesDb();
        var reloaded = fresh.Sales.Include(s => s.Payments).First(s => s.Id == held.Id);
        Assert.Empty(reloaded.Payments);
    }

    [Fact]
    public async Task AddPaymentsBatchToHoldSale_ListaVaciaLanzaArgumentException()
    {
        var service = await CreateServiceWithHeldSaleAsync(10m);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddPaymentsBatchToHoldSaleAsync(999, new List<AddPaymentRequestDto>()));
    }

    [Fact]
    public async Task AddPaymentsBatchToHoldSale_MasDeCincuentaAbonosLanzaArgumentException()
    {
        var service = await CreateServiceWithHeldSaleAsync(10m);
        var batch = Enumerable.Range(0, 51)
            .Select(_ => new AddPaymentRequestDto { PaymentMethodId = 1, AmountUSD = 0.10m, ExchangeRate = 50m })
            .ToList();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddPaymentsBatchToHoldSaleAsync(999, batch));
    }

    [Fact]
    public async Task AddPaymentsBatchToHoldSale_EfectivoFraccionadoNoPersisteNinguno()
    {
        using var salesDb = NewSalesDb();
        var service = await CreateServiceWithHeldSaleAsync(10m);
        salesDb.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo", IsActive = true, IsCash = true });
        await salesDb.SaveChangesAsync();
        salesDb.ChangeTracker.Clear();
        var held = await salesDb.Sales.FirstAsync(s => s.Status == SaleStatus.OnHold);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddPaymentsBatchToHoldSaleAsync(held.Id, new List<AddPaymentRequestDto>
            {
                new() { PaymentMethodId = 1, AmountUSD = 0m, AmountBsS = 10.50m, ExchangeRate = 50m }
            }, actingUserId: TestActorId));

        using var fresh = NewSalesDb();
        var reloaded = fresh.Sales.Include(s => s.Payments).First(s => s.Id == held.Id);
        Assert.Empty(reloaded.Payments);
    }

    // 8.185-T3 (S5): lote de abonos + cierre con política cash efectiva. El remanente USD (0.08)
    // superaba la tolerancia del gate USD-only previo; la cobertura Bs.S efectiva (28.00) liquida
    // la venta y los snapshots siguen al total EFECTIVO.
    [Fact]
    public async Task BatchAbonos_ThenEffectiveCashCheckout_CompletesWithEffectiveSnapshots()
    {
        using var salesDb = NewSalesDb();
        var invDb = NewInventoryDb();
        invDb.Products.Add(new Product
        {
            Id = 21,
            Name = "Azúcar",
            SKU = "100021",
            PriceRetailUSD = 1.44m,
            PriceUSD = 1.44m,
            StockQuantity = 50m
        });
        await invDb.SaveChangesAsync();

        salesDb.Customers.Add(new Customer { Id = 1, Name = "Consumidor Final", CedulaOrRif = "V-00000000", IsDefault = true });
        salesDb.Customers.Add(new Customer { Id = 5, Name = "Juan Perez", CedulaOrRif = "V-11223344", IsDefault = false });
        salesDb.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true },
            new PaymentMethod { Id = 2, Name = "Efectivo Bs.S", IsCash = true, IsActive = true });
        await salesDb.SaveChangesAsync();

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        settings.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultCashBsRoundingRule))
            .ReturnsAsync("VesCashFriendlyInteger");
        settings.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor))
            .ReturnsAsync("false");

        var service = new SalesService(salesDb, new InventoryService(invDb), null!, new CashDrawerService(salesDb), settings.Object, null);
        var sale = await service.StartSaleAsync(1);
        var withItems = await service.AddItemAsync(sale.Id, 21, 2m, 10m);
        Assert.Equal(2.88m, withItems.TotalUSD);
        Assert.Equal(28.00m, withItems.TotalBsS); // 14.00 por línea (nearest OFF)

        await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto { CustomerId = 5, ExchangeRate = 10m });

        var heldSale = await salesDb.Sales.FindAsync(sale.Id);
        heldSale!.ClaimedByUserId = TestActorId;
        heldSale.ClaimAction = SaleClaimAction.Editing;
        heldSale.ClaimedByUserName = "Test Actor";
        heldSale.ClaimedAtUtc = DateTime.UtcNow;
        await salesDb.SaveChangesAsync();

        // Lote de abonos por 5.00 Bs.S (2.00 + 3.00) con la regla ExactMath del lote (no interviene
        // la política: los abonos son montos explícitos del cliente).
        await service.AddPaymentsBatchToHoldSaleAsync(sale.Id, new List<AddPaymentRequestDto>
        {
            new() { PaymentMethodId = 1, AmountUSD = 0.20m, AmountBsS = 2.00m, ExchangeRate = 10m },
            new() { PaymentMethodId = 1, AmountUSD = 0.30m, AmountBsS = 3.00m, ExchangeRate = 10m }
        }, actingUserId: TestActorId);

        await service.CompleteSaleAsync(
            sale.Id,
            10m,
            new List<PaymentInfo> { new PaymentInfo(1, 0m, 23.00m, null) },
            actingUserId: TestActorId);

        var savedSale = await salesDb.Sales
            .AsNoTracking()
            .Include(s => s.Payments)
            .FirstAsync(s => s.Id == sale.Id);

        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.Equal(28.00m, savedSale.TotalBsS);
        Assert.Equal(28.00m, savedSale.FinalPaidAmountBsS);
        Assert.Equal(0m, savedSale.RoundingAdjustment);
        Assert.Equal(3, savedSale.Payments.Count);
        Assert.Equal(2.80m, savedSale.Payments.Sum(p => p.Amount));
        Assert.Equal(28.00m, savedSale.Payments.Sum(p => p.AmountBsS));
    }

    // 8.185-T3 (decisión mantenedor, guard dual): el lote de abonos que cubre el total EFECTIVO se
    // acepta aunque el acumulado USD exceda `total + 0.05` por el escalón de la política cash;
    // el resto de la semántica del lote (todo o nada, montos, efectivo entero) queda como hoy.
    [Fact]
    public async Task AddPaymentsBatchToHoldSale_CoveringEffectiveTotal_IsAccepted()
    {
        using var salesDb = NewSalesDb();
        var invDb = NewInventoryDb();
        invDb.Products.Add(new Product
        {
            Id = 22,
            Name = "Clavo",
            SKU = "100022",
            PriceRetailUSD = 1.04m,
            PriceUSD = 1.04m,
            StockQuantity = 50m
        });
        await invDb.SaveChangesAsync();

        salesDb.Customers.Add(new Customer { Id = 1, Name = "Consumidor Final", CedulaOrRif = "V-00000000", IsDefault = true });
        salesDb.Customers.Add(new Customer { Id = 5, Name = "Juan Perez", CedulaOrRif = "V-11223344", IsDefault = false });
        salesDb.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo Bs.S", IsCash = true, IsActive = true });
        await salesDb.SaveChangesAsync();

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        settings.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultCashBsRoundingRule))
            .ReturnsAsync("VesCashFriendlyInteger");
        settings.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor))
            .ReturnsAsync("true");

        var service = new SalesService(salesDb, new InventoryService(invDb), null!, new CashDrawerService(salesDb), settings.Object, null);
        var sale = await service.StartSaleAsync(1);
        var withItems = await service.AddItemAsync(sale.Id, 22, 2m, 10m);
        Assert.Equal(2.08m, withItems.TotalUSD);
        Assert.Equal(22.00m, withItems.TotalBsS); // 11.00 por unidad (never-down ON)

        await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto { CustomerId = 5, ExchangeRate = 10m });

        var heldSale = await salesDb.Sales.FindAsync(sale.Id);
        heldSale!.ClaimedByUserId = TestActorId;
        heldSale.ClaimAction = SaleClaimAction.Editing;
        heldSale.ClaimedByUserName = "Test Actor";
        heldSale.ClaimedAtUtc = DateTime.UtcNow;
        await salesDb.SaveChangesAsync();

        // El segundo abono del lote supera `total + 0.05` en USD (2.20 > 2.13) por el escalón de la
        // política; el dominio Bs.S cierra exacto (22.00), así que el lote completo se acepta.
        var result = await service.AddPaymentsBatchToHoldSaleAsync(sale.Id, new List<AddPaymentRequestDto>
        {
            new() { PaymentMethodId = 1, AmountUSD = 0m, AmountBsS = 11.00m, ExchangeRate = 10m },
            new() { PaymentMethodId = 1, AmountUSD = 0m, AmountBsS = 11.00m, ExchangeRate = 10m }
        }, actingUserId: TestActorId);

        Assert.Equal(2.20m, result.TotalPaidUSD);
        Assert.Equal(2, result.Payments.Count);

        using var fresh = NewSalesDb();
        var reloaded = fresh.Sales.Include(s => s.Payments).First(s => s.Id == sale.Id);
        Assert.Equal(2, reloaded.Payments.Count);
        Assert.Equal(22.00m, reloaded.Payments.Sum(p => p.AmountBsS));
    }
}