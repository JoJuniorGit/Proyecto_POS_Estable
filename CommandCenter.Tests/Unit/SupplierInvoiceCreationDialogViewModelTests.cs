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

    private static CreateInvoiceProductDialogViewModel CreateViewModel() =>
        new(new SupplierInvoiceLineViewModel(CreateLine()), isBsSCurrency: false);

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
            "None");
}
