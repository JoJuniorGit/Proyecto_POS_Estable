using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.ViewModels;
using Xunit;

namespace CommandCenter.Tests.Unit;

public sealed class SupplierInvoiceCreationDialogViewModelTests
{
    [Fact]
    public void Barcode_StartsEmpty_AndDoesNotInheritInvoiceCodes()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(string.Empty, viewModel.Barcode);
        Assert.DoesNotContain("SUP-1", viewModel.Barcode);
        Assert.DoesNotContain("12345", viewModel.Barcode);
    }

    [Fact]
    public void Name_AndContext_ArePrefilledFromLine()
    {
        var viewModel = CreateViewModel();

        Assert.Equal("Coffee", viewModel.Name);
        Assert.Equal("Coffee", viewModel.ProductName);
        Assert.Contains(10m.ToString("N2"), viewModel.CostInfo);
        Assert.Contains(2m.ToString("N3"), viewModel.CostInfo);
    }

    [Fact]
    public void Context_ForBsSInvoice_IncludesDocumentCost()
    {
        var line = new SupplierInvoiceLineViewModel(CreateLine(unitCostUsd: 10m, unitCostDocument: 365m));
        var viewModel = new CreateInvoiceProductDialogViewModel(line, isBsSCurrency: true);

        Assert.Contains("Bs.S", viewModel.CostInfo);
        Assert.Contains(365m.ToString("N2"), viewModel.CostInfo);
        Assert.Contains(10m.ToString("N2"), viewModel.CostInfo);
    }

    [Fact]
    public void Context_ForUsdInvoice_DoesNotShowDocumentCost()
    {
        var viewModel = CreateViewModel();

        Assert.DoesNotContain("Bs.S", viewModel.CostInfo);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("12345678901234", true)]
    [InlineData("123456789012345", false)]
    [InlineData("12A45678", false)]
    public void Save_ValidatesUniversalBarcode(string barcode, bool expectedClose)
    {
        var viewModel = CreateViewModel();
        viewModel.Name = "Harina P.A.N. 1kg";
        viewModel.Barcode = barcode;
        bool? closed = null;
        viewModel.RequestClose += result => closed = result;

        viewModel.SaveCommand.Execute(null);

        if (expectedClose)
        {
            Assert.True(closed);
        }
        else
        {
            Assert.Null(closed);
            Assert.True(viewModel.HasErrors);
        }
    }

    [Fact]
    public void Save_WithEmptyName_DoesNotClose()
    {
        var viewModel = CreateViewModel();
        viewModel.Barcode = "7591234567890";
        viewModel.Name = string.Empty;
        bool? closed = null;
        viewModel.RequestClose += result => closed = result;

        viewModel.SaveCommand.Execute(null);

        Assert.Null(closed);
        Assert.True(viewModel.HasErrors);
    }

    [Fact]
    public void Save_WithNameOver100Characters_DoesNotClose()
    {
        var viewModel = CreateViewModel();
        viewModel.Barcode = "7591234567890";
        viewModel.Name = new string('A', 101);
        bool? closed = null;
        viewModel.RequestClose += result => closed = result;

        viewModel.SaveCommand.Execute(null);

        Assert.Null(closed);
        Assert.True(viewModel.HasErrors);
    }

    [Fact]
    public void Save_WithExactly100CharacterName_Closes()
    {
        var viewModel = CreateViewModel();
        viewModel.Barcode = "7591234567890";
        viewModel.Name = new string('A', 100);
        bool? closed = null;
        viewModel.RequestClose += result => closed = result;

        viewModel.SaveCommand.Execute(null);

        Assert.True(closed);
    }

    [Fact]
    public void Save_WhileAlreadySaving_DoesNotClose()
    {
        var viewModel = CreateViewModel();
        viewModel.Barcode = "7591234567890";
        viewModel.IsSaving = true;
        bool? closed = null;
        viewModel.RequestClose += result => closed = result;

        viewModel.SaveCommand.Execute(null);

        Assert.Null(closed);
    }

    [Fact]
    public void Cancel_RaisesRequestCloseFalse()
    {
        var viewModel = CreateViewModel();
        bool? closed = null;
        viewModel.RequestClose += result => closed = result;

        viewModel.CancelCommand.Execute(null);

        Assert.False(closed);
    }

    [Fact]
    public void LinkSeeds_PopulateSelectedProductsAndMarginsWithSeedValues()
    {
        // 8.175-S3: el modal de actualización hidrata el/los producto(s) ya vinculado(s) en orden
        // con los márgenes vigentes de la línea (seed) y no con los defaults del catálogo.
        var harina = CreateProductDto(45, "Harina P.A.N. 1kg", marginRetail: 30m, marginWholesale: 15m);
        var arroz = CreateProductDto(46, "Arroz Blanco 1kg", marginRetail: 28m, marginWholesale: 12m);
        var viewModel = CreateSeededViewModel(
            (_, _) => Task.FromResult<IReadOnlyList<ProductDto>>([]),
            new InvoiceProductLinkSeed(harina, 42.5m, 18.75m),
            new InvoiceProductLinkSeed(arroz, 35m, 10m));

        Assert.True(viewModel.IsUpdateMode);
        Assert.Equal(new[] { 45, 46 }, viewModel.SelectedProducts.Select(product => product.Id).ToArray());
        Assert.Equal(45, viewModel.SelectedProduct?.Id);
        Assert.Contains("Harina P.A.N. 1kg", viewModel.SelectedProductSummary);
        Assert.Equal(2, viewModel.SelectedProductMargins.Count);
        Assert.Equal(42.5m, viewModel.SelectedProductMargins[0].MarginRetail);
        Assert.Equal(18.75m, viewModel.SelectedProductMargins[0].MarginWholesale);
        Assert.Equal(35m, viewModel.SelectedProductMargins[1].MarginRetail);
        Assert.Equal(10m, viewModel.SelectedProductMargins[1].MarginWholesale);
    }

    [Fact]
    public void LinkSeeds_WithNullMargins_FallBackToCatalogProductMargins()
    {
        // 8.175-S3: sin márgenes vigentes en la línea, la tarjeta de márgenes usa el default del catálogo.
        var harina = CreateProductDto(45, "Harina P.A.N. 1kg", marginRetail: 30m, marginWholesale: 15m);
        var viewModel = CreateSeededViewModel(
            (_, _) => Task.FromResult<IReadOnlyList<ProductDto>>([]),
            new InvoiceProductLinkSeed(harina, null, null));

        var margin = Assert.Single(viewModel.SelectedProductMargins);
        Assert.Equal(30m, margin.MarginRetail);
        Assert.Equal(15m, margin.MarginWholesale);
    }

    [Fact]
    public void UpdateMode_ShowsActualizarPrimaryActionLabel()
    {
        // 8.175-S3: la etiqueta del modo actualización cambia en singular/plural según los vínculos hidratados.
        var singleSeed = CreateSeededViewModel(
            (_, _) => Task.FromResult<IReadOnlyList<ProductDto>>([]),
            new InvoiceProductLinkSeed(CreateProductDto(45, "Harina P.A.N. 1kg"), 30m, 15m));
        var twoSeeds = CreateSeededViewModel(
            (_, _) => Task.FromResult<IReadOnlyList<ProductDto>>([]),
            new InvoiceProductLinkSeed(CreateProductDto(45, "Harina P.A.N. 1kg"), 30m, 15m),
            new InvoiceProductLinkSeed(CreateProductDto(46, "Arroz Blanco 1kg"), 28m, 12m));

        Assert.Equal("Actualizar Producto", singleSeed.PrimaryActionLabel);
        Assert.Equal("Actualizar (2) Productos", twoSeeds.PrimaryActionLabel);
    }

    [Fact]
    public void UpdateMode_RemoveSeededProduct_RemovesMarginCard()
    {
        // 8.175-S3/S4: quitar un producto hidratado elimina su tarjeta de márgenes y actualiza la etiqueta.
        var harina = CreateProductDto(45, "Harina P.A.N. 1kg", marginRetail: 30m);
        var arroz = CreateProductDto(46, "Arroz Blanco 1kg", marginRetail: 28m);
        var viewModel = CreateSeededViewModel(
            (_, _) => Task.FromResult<IReadOnlyList<ProductDto>>([]),
            new InvoiceProductLinkSeed(harina, 42.5m, 18.75m),
            new InvoiceProductLinkSeed(arroz, 35m, 10m));

        viewModel.RemoveSelectedProductCommand.Execute(harina);

        Assert.Equal(new[] { 46 }, viewModel.SelectedProducts.Select(product => product.Id).ToArray());
        Assert.Equal(46, viewModel.SelectedProduct?.Id);
        var margin = Assert.Single(viewModel.SelectedProductMargins);
        Assert.Equal(46, margin.ProductId);
        Assert.Equal(35m, margin.MarginRetail);
        Assert.Equal("Actualizar Producto", viewModel.PrimaryActionLabel);
    }

    [Fact]
    public async Task SearchAfterHydration_KeepsSeededProductSelectedWhenItAppears()
    {
        // 8.175-S3/S4: si el producto hidratado aparece en una búsqueda posterior, llega marcado
        // (sin duplicarse) para poder re-vincular o ampliar el vínculo.
        var harina = CreateProductDto(45, "Harina P.A.N. 1kg", marginRetail: 30m);
        var azucar = CreateProductDto(47, "Azúcar 1kg", marginRetail: 22m);
        var viewModel = CreateSeededViewModel(
            (_, _) => Task.FromResult<IReadOnlyList<ProductDto>>([harina, azucar]),
            new InvoiceProductLinkSeed(harina, 42.5m, 18.75m));

        viewModel.SearchQuery = "1kg";
        await viewModel.SearchCatalogAsync();

        Assert.Equal(2, viewModel.SelectableSearchResults.Count);
        Assert.True(viewModel.SelectableSearchResults.Single(item => item.Id == 45).IsSelected);
        Assert.False(viewModel.SelectableSearchResults.Single(item => item.Id == 47).IsSelected);
        Assert.Single(viewModel.SelectedProducts);
        Assert.Single(viewModel.SelectedProductMargins);
    }

    [Fact]
    public void WithoutSeeds_IsUpdateModeFalseAndLabelStaysVincular()
    {
        // 8.175-S3: sin semillas el modal conserva el flujo de vinculación original.
        var viewModel = CreateSeededViewModel((_, _) => Task.FromResult<IReadOnlyList<ProductDto>>([]));

        Assert.False(viewModel.IsUpdateMode);
        Assert.Equal("Vincular Producto", viewModel.PrimaryActionLabel);
    }

    private static CreateInvoiceProductDialogViewModel CreateViewModel() =>
        new(new SupplierInvoiceLineViewModel(CreateLine()), isBsSCurrency: false);

    private static CreateInvoiceProductDialogViewModel CreateSeededViewModel(
        Func<string, CancellationToken, Task<IReadOnlyList<ProductDto>>> searchProductsAsync,
        params InvoiceProductLinkSeed[] linkSeeds) =>
        new(new SupplierInvoiceLineViewModel(CreateLine()), isBsSCurrency: false, searchProductsAsync, linkSeeds);

    private static ProductDto CreateProductDto(
        int id,
        string name,
        decimal marginRetail = 30m,
        decimal marginWholesale = 15m) => new()
        {
            Id = id,
            Name = name,
            SKU = $"SKU-{id}",
            CostPriceUSD = 8m,
            ProfitMarginRetail = marginRetail,
            ProfitMarginWholesale = marginWholesale,
            HasWholesale = true,
            StockQuantity = 10m
        };

    private static SupplierInvoiceLineDto CreateLine(
        decimal unitCostUsd = 10m,
        decimal unitCostDocument = 10m) => new(
            1,
            "SUP-1",
            "12345",
            "Coffee",
            2m,
            unitCostDocument,
            unitCostUsd,
            "New",
            null,
            0m,
            0m,
            0m,
            0m,
            null,
            null,
            null,
            null,
            false,
            "None",
            null,
            null,
            null);
}
