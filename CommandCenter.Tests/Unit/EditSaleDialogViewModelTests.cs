using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class EditSaleDialogViewModelTests
{
    private static SaleDto CreateSale(decimal totalPaid, decimal? itemUnitPrice = null)
    {
        var items = new List<SaleItemDto>();
        if (itemUnitPrice.HasValue)
        {
            items.Add(new SaleItemDto
            {
                Id = 1,
                ProductId = 100,
                ProductName = "Producto A",
                Quantity = 1m,
                UnitPrice = itemUnitPrice.Value,
                Subtotal = itemUnitPrice.Value,
                UnitPriceBsS = itemUnitPrice.Value * 50m,
                SubtotalBsS = itemUnitPrice.Value * 50m
            });
        }

        return new SaleDto
        {
            Id = 1,
            Status = "OnHold",
            TotalUSD = items.Sum(i => i.Subtotal),
            TotalPaidUSD = totalPaid,
            AppliedRate = 50m,
            PriceListType = "Retail",
            Items = items
        };
    }

    [Fact]
    public async Task LoadSale_PopulatesItemsAndSummary()
    {
        var vm = new EditSaleDialogViewModel();
        vm.LoadSale(CreateSale(totalPaid: 10m, itemUnitPrice: 5m), exchangeRate: 50m);

        Assert.Single(vm.Items);
        Assert.Equal("Producto A", vm.Items[0].ProductName);
        Assert.Equal(5m, vm.Items[0].UnitPrice);
        Assert.Equal(5m, vm.Items[0].Subtotal);
        Assert.False(string.IsNullOrWhiteSpace(vm.NewTotalText));
        Assert.False(string.IsNullOrWhiteSpace(vm.TotalPaidText));
        Assert.False(string.IsNullOrWhiteSpace(vm.RemainingText));
        vm.Dispose();
    }

    [Fact]
    public void Save_WhenNewTotalBelowPaid_SetsValidationError()
    {
        var vm = new EditSaleDialogViewModel();
        vm.LoadSale(CreateSale(totalPaid: 100m, itemUnitPrice: 5m), exchangeRate: 50m);

        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasValidationError);
        Assert.False(vm.HasChanges);
        Assert.Contains("no puede ser menor", vm.ValidationMessage);
        vm.Dispose();
    }

    [Fact]
    public void Save_WhenNewTotalCoversPaid_SetsHasChangesAndModifiedItems()
    {
        var vm = new EditSaleDialogViewModel();
        vm.LoadSale(CreateSale(totalPaid: 2m, itemUnitPrice: 5m), exchangeRate: 50m);

        vm.SaveCommand.Execute(null);

        Assert.False(vm.HasValidationError);
        Assert.True(vm.HasChanges);
        Assert.NotNull(vm.ModifiedItems);
        var item = Assert.Single(vm.ModifiedItems!);
        Assert.Equal(100, item.ProductId);
        Assert.Equal(1m, item.Quantity);
        vm.Dispose();
    }

    [Fact]
    public void SelectSuggestion_WithExistingProduct_IncrementsQuantity()
    {
        var vm = new EditSaleDialogViewModel();
        vm.LoadSale(CreateSale(totalPaid: 10m, itemUnitPrice: 5m), exchangeRate: 50m);

        vm.Suggestions = new List<ProductQuickInfoDto>
        {
            new ProductQuickInfoDto { Id = 100, Name = "Producto A", PriceUSD = 5m, PriceWholesaleUSD = 4m, MinWholesaleQuantity = 6m }
        };
        vm.SelectedSuggestion = vm.Suggestions[0];
        vm.SelectSuggestionCommand.Execute(null);

        var item = Assert.Single(vm.Items);
        Assert.Equal(2m, item.Quantity);
        vm.Dispose();
    }

    [Fact]
    public void SelectSuggestion_WithNewProduct_AddsItem()
    {
        var vm = new EditSaleDialogViewModel();
        vm.LoadSale(CreateSale(totalPaid: 0m), exchangeRate: 50m);

        vm.Suggestions = new List<ProductQuickInfoDto>
        {
            new ProductQuickInfoDto { Id = 200, Name = "Producto B", PriceUSD = 3m, PriceWholesaleUSD = 2m, MinWholesaleQuantity = 6m }
        };
        vm.SelectedSuggestion = vm.Suggestions[0];
        vm.SelectSuggestionCommand.Execute(null);

        var item = Assert.Single(vm.Items);
        Assert.Equal(200, item.ProductId);
        Assert.Equal(1m, item.Quantity);
        vm.Dispose();
    }
}