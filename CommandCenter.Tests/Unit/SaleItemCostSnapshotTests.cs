using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;

namespace CommandCenter.Tests.Unit;

public class SaleItemCostSnapshotTests
{
    [Fact]
    public async Task AddItemAsync_PersistsUnitCostSnapshot_FromCatalogCost()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var inventoryMock = new Mock<IInventoryService>();
        var mediatorMock = new Mock<MediatR.IMediator>();
        var cashDrawerMock = new Mock<ICashDrawerService>();
        var settingsMock = new Mock<Core.Interfaces.ISystemSettingsService>();

        cashDrawerMock
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new Sales.Module.DTOs.CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(88).WithSku("SKU-88").WithName("Con Costo").WithCostAndMargin(2.50m, 40.00m).Build();
        inventoryMock.Setup(i => i.GetProductByIdAsync(88)).ReturnsAsync(product);

        var sale = await service.StartSaleAsync();
        await service.AddItemAsync(sale.Id, 88, 1, 842.21m);

        var item = context.Set<Sales.Module.Entities.SaleItem>().Single(i => i.ProductId == 88);

        // El costo del catalogo viaja con la linea: sin esto, el calculo de ganancias (Paso 14)
        // recalcularia COGS con el costo actual y violaria la inmutabilidad del historial.
        Assert.Equal(2.50m, item.UnitCostUSD);
    }

    [Fact]
    public void SaleItemCost_IsNotExposedToClients()
    {
        // El costo es dato sensible: no debe filtrarse por ningun DTO que viaja al cliente
        // (regla de enmascaramiento de costos/margenes para cajeros).
        Assert.Null(typeof(SaleItemDto).GetProperty("UnitCostUSD", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
        Assert.Null(typeof(SaleDto).GetProperty("UnitCostUSD", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
    }
}
