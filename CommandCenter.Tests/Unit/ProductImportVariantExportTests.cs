using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ProductImportVariantExportTests
{
    private (InventoryService service, InventoryDbContext context) CreateService()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var userMock = new Mock<ICurrentUserService>();
        userMock.Setup(u => u.CanMutateCatalog).Returns(true);
        var service = new InventoryService(context, userMock.Object);
        return (service, context);
    }

    [Fact]
    public async Task ExportProductsAsync_Csv_IncludesVariantColumnsAndTypes()
    {
        var (service, context) = CreateService();

        context.Products.AddRange(
            new Product { Id = 1, SKU = "GRP-1", Name = "Grupo A", IsGroupHeader = true, GroupKey = "GRP-A", IsActive = true },
            new Product { Id = 2, SKU = "VAR-1", Name = "Variante 1", ParentProductId = 1, IsStockShared = false, ConversionFactor = 2.0000m, IsActive = true },
            new Product { Id = 3, SKU = "NORM-1", Name = "Normal 1", IsActive = true }
        );
        await context.SaveChangesAsync();

        var bytes = await service.ExportProductsAsync("csv", activeOnly: false);
        var csv = Encoding.UTF8.GetString(bytes);

        Assert.Contains("TipoProducto;Grupo;CompartirStock;FactorConversion", csv);
        Assert.Contains("GRP-1;Grupo A", csv);
        var groupLine = csv.Split('\n').First(l => l.Contains("GRP-1"));
        Assert.Contains("Grupo", groupLine);
        var variantLine = csv.Split('\n').First(l => l.Contains("VAR-1"));
        Assert.Contains("Variante", variantLine);
        Assert.Contains("GRP-A", variantLine);
        Assert.Contains("2", variantLine);
        var normalLine = csv.Split('\n').First(l => l.Contains("NORM-1"));
        Assert.Contains("Normal", normalLine);
    }

    [Fact]
    public async Task GenerateTemplateAsync_Csv_IncludesVariantHeaders()
    {
        var (service, _) = CreateService();

        var bytes = await service.GenerateTemplateAsync("csv");
        var csv = Encoding.UTF8.GetString(bytes);

        Assert.Contains("TipoProducto;Grupo;CompartirStock;FactorConversion", csv);
    }
}