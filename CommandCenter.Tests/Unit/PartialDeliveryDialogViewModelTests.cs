using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PartialDeliveryDialogViewModelTests
{
    [Fact]
    public void InputQuantity_WhenAbovePending_ClampsAndShowsValidationError()
    {
        var viewModel = CreateViewModel();
        var line = Assert.Single(viewModel.Items);

        line.InputQuantityText = "4";

        Assert.Equal(3m, line.InputQuantity);
        Assert.Equal("3", line.InputQuantityText);
        Assert.Equal("Supera la cantidad pendiente", line.ErrorMessage);
        Assert.False(line.IsValid);
        Assert.False(viewModel.CanConfirm);
    }

    [Fact]
    public void InputQuantity_WhenNegative_ClampsToZeroAndShowsValidationError()
    {
        var viewModel = CreateViewModel();
        var line = Assert.Single(viewModel.Items);

        line.InputQuantityText = "-1";

        Assert.Equal(0m, line.InputQuantity);
        Assert.Equal("0", line.InputQuantityText);
        Assert.Equal("La cantidad no puede ser negativa.", line.ErrorMessage);
        Assert.False(line.IsValid);
        Assert.False(viewModel.CanConfirm);
    }

    [Fact]
    public void InputQuantityText_WhenCleared_ResetsEffectiveQuantityToZero()
    {
        var viewModel = CreateViewModel();
        var line = Assert.Single(viewModel.Items);
        line.InputQuantity = 1m;

        line.InputQuantityText = string.Empty;

        Assert.Equal(0m, line.InputQuantity);
        Assert.True(line.IsValid);
        Assert.False(viewModel.CanConfirm);
    }

    [Fact]
    public void InputQuantityText_WhenFractionalValueIsEntered_PreservesDecimalPrecision()
    {
        var viewModel = CreateViewModel();
        var line = Assert.Single(viewModel.Items);
        var typedValue = $"1{CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}25";

        line.InputQuantityText = typedValue;

        Assert.Equal(1.25m, line.InputQuantity);
        Assert.Equal(typedValue, line.InputQuantityText);
        Assert.True(viewModel.CanConfirm);
    }

    [Fact]
    public void CanConfirm_RequiresAtLeastOnePositiveQuantityAndAllLinesValid()
    {
        var viewModel = CreateViewModel(includeSecondItem: true);
        var lines = viewModel.Items.ToArray();

        Assert.False(viewModel.CanConfirm);

        lines[0].InputQuantity = 1.5m;
        Assert.True(viewModel.CanConfirm);

        lines[1].InputQuantity = 4m;
        Assert.False(viewModel.CanConfirm);

        lines[1].InputQuantity = 2m;
        Assert.True(viewModel.CanConfirm);

        lines[0].InputQuantity = 0m;
        lines[1].InputQuantity = 0m;
        Assert.False(viewModel.CanConfirm);
    }

    [Fact]
    public void ConfirmCommand_MapsOnlyPositiveQuantitiesAndNotesToResult()
    {
        var viewModel = CreateViewModel(includeSecondItem: true);
        var lines = viewModel.Items.ToArray();
        lines[0].InputQuantity = 1.25m;
        viewModel.Notes = "  Cliente retirará el resto mañana.  ";

        viewModel.ConfirmCommand.Execute(null);

        Assert.NotNull(viewModel.Result);
        var resultItem = Assert.Single(viewModel.Result!.Items);
        Assert.Equal(101, resultItem.SaleItemId);
        Assert.Equal(1.25m, resultItem.Quantity);
        Assert.Equal("Cliente retirará el resto mañana.", viewModel.Result.Notes);
    }

    [Fact]
    public void Constructor_WithPendingDraft_RestoresQuantitiesAndNotes()
    {
        var pickup = new PendingPickupClientDto
        {
            Items = new List<PendingPickupItemDto>
            {
                new()
                {
                    SaleItemId = 101,
                    ProductId = 1,
                    ProductName = "Arroz",
                    PendingQuantity = 3m
                }
            },
            PendingDraft = new PartialDeliveryDialogResult
            {
                Items = new List<PartialDeliveryItemRequestDto>
                {
                    new() { SaleItemId = 101, Quantity = 2m }
                },
                Notes = "Retiro coordinado"
            }
        };

        var viewModel = new PartialDeliveryDialogViewModel(pickup);

        Assert.Equal(2m, Assert.Single(viewModel.Items).InputQuantity);
        Assert.Equal("Retiro coordinado", viewModel.Notes);
        Assert.True(viewModel.CanConfirm);
        Assert.Null(pickup.PendingDraft);
    }

    private static PartialDeliveryDialogViewModel CreateViewModel(bool includeSecondItem = false)
    {
        var items = new List<PendingPickupItemDto>
        {
            new()
            {
                SaleItemId = 101,
                ProductId = 1,
                ProductName = "Arroz",
                Quantity = 5m,
                PendingQuantity = 3m
            }
        };

        if (includeSecondItem)
        {
            items.Add(new PendingPickupItemDto
            {
                SaleItemId = 102,
                ProductId = 2,
                ProductName = "Harina",
                Quantity = 4m,
                PendingQuantity = 3m
            });
        }

        return new PartialDeliveryDialogViewModel(new PendingPickupClientDto { Items = items });
    }
}
