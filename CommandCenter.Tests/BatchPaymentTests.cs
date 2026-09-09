using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests;

// 8.29-A05: abonos atómicos por lote. Un lote se valida ANTES de persistir: si CUALQUIER
// abono falla (monto, límite acumulado, efectivo fraccionado, lote vacío o > 50), NINGUNO
// se persiste (todo o nada), porque todos los abonos comparten una única transacción.
public class BatchPaymentTests
{
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
        });

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

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddPaymentsBatchToHoldSaleAsync(held.Id, new List<AddPaymentRequestDto>
            {
                new() { PaymentMethodId = 1, AmountUSD = 6m, AmountBsS = 300m, ExchangeRate = 50m },
                new() { PaymentMethodId = 1, AmountUSD = 15m, AmountBsS = 750m, ExchangeRate = 50m }
            }));

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

        // Cada abono por separado cabe (4 <= 10.05), pero el ACUMULADO del lote (4+4+4 > 10) no.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddPaymentsBatchToHoldSaleAsync(held.Id, new List<AddPaymentRequestDto>
            {
                new() { PaymentMethodId = 1, AmountUSD = 4m, AmountBsS = 200m, ExchangeRate = 50m },
                new() { PaymentMethodId = 1, AmountUSD = 4m, AmountBsS = 200m, ExchangeRate = 50m },
                new() { PaymentMethodId = 1, AmountUSD = 4m, AmountBsS = 200m, ExchangeRate = 50m }
            }));

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

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddPaymentsBatchToHoldSaleAsync(held.Id, new List<AddPaymentRequestDto>
            {
                new() { PaymentMethodId = 1, AmountUSD = 0m, AmountBsS = 10.50m, ExchangeRate = 50m }
            }));

        using var fresh = NewSalesDb();
        var reloaded = fresh.Sales.Include(s => s.Payments).First(s => s.Id == held.Id);
        Assert.Empty(reloaded.Payments);
    }
}