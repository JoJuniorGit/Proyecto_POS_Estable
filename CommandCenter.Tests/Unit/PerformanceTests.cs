using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PerformanceTests
{
    private IMemoryCache CreateMemoryCache()
    {
        return new MemoryCache(new MemoryCacheOptions());
    }

    // 1. Catalog Pagination With 10,000 Products Executes Fast and Returns Exact Page Size
    [Fact]
    public async Task Catalog_Pagination_With_10000_Products_Executes_Fast()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext("Perf_Catalog_10k_" + Guid.NewGuid());

        // Generar 10,000 productos
        var products = new List<Product>(10000);
        for (int i = 1; i <= 10000; i++)
        {
            products.Add(new Product
            {
                Id = i,
                SKU = i.ToString("D5"),
                Name = $"Producto {i:D5}",
                CostPriceUSD = 1.00m + (i % 10),
                ProfitMarginRetail = 30m,
                PriceRetailUSD = 1.30m + (i % 10),
                PriceUSD = 1.30m + (i % 10),
                StockQuantity = 100,
                IsActive = true,
                IsDeleted = false
            });
        }
        await context.Products.AddRangeAsync(products);
        await context.SaveChangesAsync();

        var mockCurrentUserService = new Mock<ICurrentUserService>();
        var service = new InventoryService(context, mockCurrentUserService.Object, CreateMemoryCache());

        var sw = Stopwatch.StartNew();
        var result = await service.GetProductsPagedAsync(
            filter: "Producto 05",
            page: 1,
            pageSize: 25,
            statusFilter: "active",
            sortBy: "name",
            isDescending: false);
        sw.Stop();

        Assert.Equal(25, result.Items.Count());
        Assert.True(result.TotalCount >= 100, "Debe encontrar los productos coincidentes con 'Producto 05'.");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"La paginación con 10.000 productos tardó {sw.ElapsedMilliseconds}ms, debe ser < 1500ms.");
    }

    // 2. RecalculateTotal With 15 Items Executes Single Batch Lookup (No N+1)
    [Fact]
    public async Task RecalculateTotal_With_15_Items_Executes_Single_Batch_Lookup()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext("Perf_Recalculate_15Items_" + Guid.NewGuid());
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        context.Customers.Add(new Customer
        {
            Id = 2,
            Name = "Cliente Real Prueba",
            CedulaOrRif = "V-12345678",
            IsDefault = false,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<Sales.Module.Interfaces.ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        int batchCallsCount = 0;
        int singleCallsCount = 0;

        var productIdsToFetch = Enumerable.Range(1, 15).ToList();
        var returnedProducts = productIdsToFetch.Select(id => new Product
        {
            Id = id,
            SKU = $"SKU-{id}",
            Name = $"Item {id}",
            CostPriceUSD = 2.00m,
            ProfitMarginRetail = 50m,
            PriceRetailUSD = 3.00m,
            PriceUSD = 3.00m,
            IsActive = true
        }).ToList();

        mockInventory.Setup(i => i.GetProductsByIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync((IEnumerable<int> ids) =>
            {
                Interlocked.Increment(ref batchCallsCount);
                return returnedProducts.Where(p => ids.Contains(p.Id)).ToList();
            });

        mockInventory.Setup(i => i.GetProductByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) =>
            {
                Interlocked.Increment(ref singleCallsCount);
                return returnedProducts.FirstOrDefault(p => p.Id == id);
            });

        var salesService = new Sales.Module.Services.SalesService(
            context,
            mockInventory.Object,
            mockMediator.Object,
            mockCashDrawer.Object,
            mockSettings.Object,
            cache: CreateMemoryCache());

        // Iniciar venta y ponerla en espera (OnHold) asignándole un cliente real
        var saleDto = await salesService.StartSaleAsync();
        await salesService.HoldSaleAsync(saleDto.Id, new HoldSaleRequestDto
        {
            CustomerId = 2,
            ExchangeRate = 50m
        });

        // Resetear contadores de llamadas antes de la operación en lote
        Interlocked.Exchange(ref batchCallsCount, 0);
        Interlocked.Exchange(ref singleCallsCount, 0);

        // Actualizar la venta con 15 ítems en una sola solicitud en lote
        var updateRequest = new UpdateSaleItemsRequestDto
        {
            Items = productIdsToFetch.Select(id => new UpdateSaleItemDto
            {
                ProductId = id,
                Quantity = 1m,
                UnitPrice = 3.00m
            }).ToList()
        };

        var updatedSale = await salesService.UpdateSaleItemsAsync(saleDto.Id, updateRequest);

        // Verificamos que se ejecutó en modo batch (<= 2 llamadas batch) y 0 consultas individuales N+1
        Assert.True(batchCallsCount >= 1 && batchCallsCount <= 2, $"Se ejecutaron {batchCallsCount} consultas batch.");
        Assert.Equal(0, singleCallsCount);
        Assert.Equal(45.00m, updatedSale.TotalUSD); // 15 items * $3.00
    }

    // 3. PaymentMethod Cache Hit And Invalidation Works Cleanly
    [Fact]
    public async Task PaymentMethod_Cache_Hit_And_Invalidation_Works_Cleanly()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext("Perf_PaymentMethod_Cache_" + Guid.NewGuid());
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var cache = CreateMemoryCache();
        var service = new PaymentMethodService(context, cache);

        // 1ra llamada: Llena la caché desde BD
        var methods1 = (await service.GetActiveMethodsAsync()).ToList();
        Assert.NotEmpty(methods1);

        // Modificamos directamente la entidad en el context sin pasar por el servicio
        var pm = await context.PaymentMethods.FirstAsync(p => p.IsActive);
        var originalName = pm.Name;

        // 2da llamada: Debe venir de la caché en memoria (Hit inmediato < 50ms)
        var sw = Stopwatch.StartNew();
        var methodsCached = (await service.GetActiveMethodsAsync()).ToList();
        sw.Stop();

        Assert.Equal(methods1.Count, methodsCached.Count);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Cache hit tardó {sw.ElapsedMilliseconds}ms, debe ser inmediato.");

        // Mutación a través del servicio: Invalida la caché
        pm.Name = "Nombre Actualizado";
        await service.UpdateAsync(pm);

        // 3ra llamada tras invalidación: Refleja el nuevo valor desde BD
        var methodsUpdated = (await service.GetActiveMethodsAsync()).ToList();
        var updatedItem = methodsUpdated.First(m => m.Id == pm.Id);
        Assert.Equal("Nombre Actualizado", updatedItem.Name);
    }

    // 4. ExchangeRate Cache Hit And Invalidation Works Cleanly
    [Fact]
    public async Task ExchangeRate_Cache_Hit_And_Invalidation_Works_Cleanly()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext("Perf_ExchangeRate_Cache_" + Guid.NewGuid());
        var cache = CreateMemoryCache();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        context.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = today,
            Rate = 55.50m,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = new InventoryService(context, null, cache);

        // 1ra llamada: Consulta BD y almacena en caché
        var rate1 = await service.GetTodayExchangeRateAsync();
        Assert.Equal(55.50m, rate1);

        // Actualizamos BD directamente
        var record = await context.ExchangeRateHistory.FirstAsync(r => r.Date == today);
        record.Rate = 60.00m;
        await context.SaveChangesAsync();

        // 2da llamada: Debe retornar el valor en caché (55.50)
        var rateCached = await service.GetTodayExchangeRateAsync();
        Assert.Equal(55.50m, rateCached);

        // Invalidación explícita
        service.InvalidateTodayExchangeRateCache();

        // 3ra llamada: Refresca desde BD (60.00)
        var rateFresh = await service.GetTodayExchangeRateAsync();
        Assert.Equal(60.00m, rateFresh);
    }

    // 5. DefaultCustomer Cache Hit And Invalidation Works Cleanly
    [Fact]
    public async Task DefaultCustomer_Cache_Hit_And_Invalidation_Works_Cleanly()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext("Perf_DefaultCustomer_Cache_" + Guid.NewGuid());
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var cache = CreateMemoryCache();

        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<Sales.Module.Interfaces.ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var salesService = new Sales.Module.Services.SalesService(
            context,
            mockInventory.Object,
            mockMediator.Object,
            mockCashDrawer.Object,
            mockSettings.Object,
            cache: cache);

        // 1ra llamada: Cachea Consumidor Final
        var defaultCustomer = await salesService.GetDefaultCustomerAsync();
        Assert.NotNull(defaultCustomer);
        Assert.Equal("V-00000000", defaultCustomer.CedulaOrRif);

        // 2da llamada: Obtiene desde caché
        var sw = Stopwatch.StartNew();
        var cachedDefault = await salesService.GetDefaultCustomerAsync();
        sw.Stop();

        Assert.Equal(defaultCustomer.Name, cachedDefault.Name);
        Assert.True(sw.ElapsedMilliseconds < 50);

        // Actualización del cliente por defecto a través del servicio
        await salesService.UpdateCustomerAsync(defaultCustomer.Id, new UpdateCustomerDto
        {
            CedulaOrRif = "V-00000000",
            Name = "Consumidor Final VIP",
            Phone = "04141234567",
            CreditLimitUSD = 0,
            IsActive = true
        });

        // 3ra llamada: Al invalidarse la caché, retorna el nombre actualizado
        var updatedDefault = await salesService.GetDefaultCustomerAsync();
        Assert.Equal("Consumidor Final VIP", updatedDefault.Name);
    }

    // 6. AsSplitQuery Prevents Cartesian Product Duplication In Sales
    [Fact]
    public async Task AsSplitQuery_Prevents_Cartesian_Product_Duplication_In_Sales()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext("Perf_SplitQuery_Sales_" + Guid.NewGuid());
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        context.Customers.Add(new Customer
        {
            Id = 2,
            Name = "Cliente Real Prueba",
            CedulaOrRif = "V-12345678",
            IsDefault = false,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var cache = CreateMemoryCache();

        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<Sales.Module.Interfaces.ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetTodayExchangeRateAsync()).ReturnsAsync(50m);
        mockInventory.Setup(i => i.GetProductByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => new Product { Id = id, SKU = $"SKU{id}", Name = $"Prod {id}", PriceUSD = 10m, PriceRetailUSD = 10m, IsActive = true });
        mockInventory.Setup(i => i.GetProductsByIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync((IEnumerable<int> ids) => ids.Select(id => new Product { Id = id, SKU = $"SKU{id}", Name = $"Prod {id}", PriceUSD = 10m, PriceRetailUSD = 10m, IsActive = true }).ToList());

        var salesService = new Sales.Module.Services.SalesService(
            context,
            mockInventory.Object,
            mockMediator.Object,
            mockCashDrawer.Object,
            mockSettings.Object,
            cache: cache);

        // Crear una venta con 3 items
        var saleDto = await salesService.StartSaleAsync();
        await salesService.AddItemAsync(saleDto.Id, 1, 1m, 50m);
        await salesService.AddItemAsync(saleDto.Id, 2, 1m, 50m);
        await salesService.AddItemAsync(saleDto.Id, 3, 1m, 50m);

        // Poner en espera con 2 pagos parciales
        var holdRequest = new HoldSaleRequestDto
        {
            CustomerId = 2,
            ExchangeRate = 50m,
            InitialPayments = new List<AddPaymentRequestDto>
            {
                new AddPaymentRequestDto { PaymentMethodId = 1, AmountUSD = 10m, AmountBsS = 500m, ExchangeRate = 50m },
                new AddPaymentRequestDto { PaymentMethodId = 2, AmountUSD = 5m, AmountBsS = 250m, ExchangeRate = 50m }
            }
        };
        await salesService.HoldSaleAsync(saleDto.Id, holdRequest);

        // Consultar ventas pendientes (usa AsSplitQuery internamente)
        var pendingSales = (await salesService.GetPendingSalesAsync()).ToList();
        var pending = pendingSales.FirstOrDefault(s => s.Id == saleDto.Id);

        Assert.NotNull(pending);
        Assert.Equal(3, pending.Items.Count);
        Assert.Equal(2, pending.Payments.Count);
        Assert.Equal(30m, pending.TotalUSD);
        Assert.Equal(15m, pending.TotalPaidUSD);
        Assert.Equal(15m, pending.RemainingBalanceUSD);
    }
}
