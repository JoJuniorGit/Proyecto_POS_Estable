using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.DTOs;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public partial class ProductVariantsTests
{
    [Fact]
    public async Task ProductDialogViewModel_LoadsParentProducts_AndSelectsExistingParent()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var parents = new List<ProductDto>
        {
            new ProductDto
            {
                Id = 10,
                Name = "Jugo 1L Frutas",
                SKU = "GRP-JUGO",
                CostPriceUSD = 1.00m,
                ProfitMarginRetail = 50m,
                PriceRetailUSD = 1.50m,
                IsGroupHeader = true
            },
            new ProductDto
            {
                Id = 20,
                Name = "Yogurt 1L Sabores",
                SKU = "GRP-YOG",
                CostPriceUSD = 2.00m,
                ProfitMarginRetail = 40m,
                PriceRetailUSD = 2.80m,
                IsGroupHeader = true
            }
        };

        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(parents);

        var existingVariant = new Product
        {
            Id = 105,
            Name = "Jugo 1L Naranja",
            SKU = "7591001",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 50m,
            PriceRetailUSD = 1.50m,
            ParentProductId = 10
        };

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, existingVariant);
        await vm.LoadMetadataAsync();

        Assert.Equal(3, vm.ParentProducts.Count); // Ninguno (0) + 2 parents
        Assert.Equal(0, vm.ParentProducts[0].Id);
        Assert.Equal("Ninguno (Producto Independiente)", vm.ParentProducts[0].Name);

        Assert.NotNull(vm.SelectedParentProduct);
        Assert.Equal(10, vm.SelectedParentProduct.Id);
        Assert.True(vm.IsVariant);
        Assert.True(vm.IsInheritingPricing);
        Assert.False(vm.CanEditPricing);
    }

}
