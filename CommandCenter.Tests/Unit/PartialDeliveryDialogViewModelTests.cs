using System;
using System.Collections.Generic;
using System.Linq;
using Desktop.Client.Services;
using Desktop.Client.Validation;
using Desktop.Client.ViewModels;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PartialDeliveryDialogViewModelTests
{
    [Fact]
    public void QuantityText_WhenAbovePending_KeepsTypedValueAndBlocksConfirmation()
    {
        var viewModel = CreateViewModel();
        var line = Assert.Single(viewModel.Items);

        line.QuantityText = "4";

        Assert.Equal(4m, line.InputQuantity);
        Assert.Equal("4", line.QuantityText);
        Assert.Equal("Máximo: 3", line.ErrorMessage);
        Assert.False(line.IsValid);
        Assert.False(viewModel.CanConfirm);
        Assert.Equal("Revisa las cantidades marcadas", viewModel.SummaryText);
    }

    [Fact]
    public void QuantityText_WhenNegative_RejectsAndShowsValidationError()
    {
        var viewModel = CreateViewModel();
        var line = Assert.Single(viewModel.Items);

        line.QuantityText = "-1";

        Assert.Equal(0m, line.InputQuantity);
        Assert.Equal("La cantidad no puede ser negativa.", line.ErrorMessage);
        Assert.False(line.IsValid);
        Assert.False(viewModel.CanConfirm);
    }

    [Fact]
    public void QuantityText_WhenInvalidText_RejectsAndShowsValidationError()
    {
        var viewModel = CreateViewModel();
        var line = Assert.Single(viewModel.Items);

        line.QuantityText = "abc";

        Assert.Equal(0m, line.InputQuantity);
        Assert.Equal("Ingrese solo números enteros.", line.ErrorMessage);
        Assert.False(viewModel.CanConfirm);
    }

    [Fact]
    public void QuantityText_WhenCleared_LeavesFieldEmptyAndResetsEffectiveQuantity()
    {
        var viewModel = CreateViewModel();
        var line = Assert.Single(viewModel.Items);
        line.InputQuantity = 1m;

        line.QuantityText = string.Empty;

        Assert.Equal(0m, line.InputQuantity);
        Assert.Equal(string.Empty, line.QuantityText);
        Assert.True(line.IsValid);
        Assert.False(viewModel.CanConfirm);
    }

    [Fact]
    public void QuantityText_WhenProductAllowsDecimals_PreservesDecimalPrecision()
    {
        var viewModel = CreateViewModel(firstPending: 3.5m);
        var line = Assert.Single(viewModel.Items);

        line.QuantityText = "1.25";

        Assert.True(line.AllowsDecimals);
        Assert.Equal(1.25m, line.InputQuantity);
        Assert.Equal("1.25", line.QuantityText);
        Assert.True(viewModel.CanConfirm);
    }

    [Fact]
    public void QuantityText_WhenWholeProductReceivesDecimal_RejectsIt()
    {
        var viewModel = CreateViewModel();
        var line = Assert.Single(viewModel.Items);

        line.QuantityText = "1.5";

        Assert.False(line.AllowsDecimals);
        Assert.Equal(0m, line.InputQuantity);
        Assert.Equal("Este producto solo admite cantidades enteras.", line.ErrorMessage);
        Assert.False(viewModel.CanConfirm);
    }

    [Fact]
    public void CanConfirm_RequiresAtLeastOnePositiveQuantityAndAllLinesValid()
    {
        var viewModel = CreateViewModel(includeSecondItem: true, firstPending: 3.5m);
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
        var viewModel = CreateViewModel(includeSecondItem: true, firstPending: 3.5m);
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
    public void SetAllToMaxCommand_FillsEveryLineAndShowsFullDelivery()
    {
        var viewModel = CreateViewModel(includeSecondItem: true);

        viewModel.SetAllToMaxCommand.Execute(null);

        Assert.All(viewModel.Items, line => Assert.Equal(line.PendingQuantity, line.InputQuantity));
        Assert.Equal(6m, viewModel.TotalToDeliver);
        Assert.Equal("Entregando 6 de 6 unidades · Retiro total", viewModel.SummaryText);
        Assert.Equal("#15803D", viewModel.SummaryColor);
        Assert.True(viewModel.CanConfirm);
    }

    [Fact]
    public void ClearAllCommand_ResetsEveryLineAndShowsEmptyWarning()
    {
        var viewModel = CreateViewModel(includeSecondItem: true);
        viewModel.SetAllToMaxCommand.Execute(null);

        viewModel.ClearAllCommand.Execute(null);

        Assert.All(viewModel.Items, line => Assert.Equal(0m, line.InputQuantity));
        Assert.Equal("Ingresa al menos una cantidad para entregar", viewModel.SummaryText);
        Assert.Equal("#B45309", viewModel.SummaryColor);
        Assert.False(viewModel.CanConfirm);
    }

    [Fact]
    public void SetMaxCommand_AssignsTheLinePendingQuantity()
    {
        var viewModel = CreateViewModel(firstPending: 3.5m);
        var line = Assert.Single(viewModel.Items);

        line.SetMaxCommand.Execute(null);

        Assert.Equal(3.5m, line.InputQuantity);
        Assert.Equal("✓ Completo", line.RemainingText);
        Assert.Equal("#DCFCE7", line.RemainingBadgeBackground);
        Assert.True(viewModel.CanConfirm);
    }

    [Fact]
    public void RemainingText_ReflectsPendingMinusDelivered()
    {
        var viewModel = CreateViewModel();
        var line = Assert.Single(viewModel.Items);

        line.InputQuantity = 1m;
        Assert.Equal("2 pend.", line.RemainingText);

        line.InputQuantity = 3m;
        Assert.Equal("✓ Completo", line.RemainingText);
        Assert.Equal("#DCFCE7", line.RemainingBadgeBackground);

        line.InputQuantity = 4m;
        Assert.Equal("Excede", line.RemainingText);
        Assert.Equal("#FEE2E2", line.RemainingBadgeBackground);
    }

    [Fact]
    public void SummaryText_DistinguishesPartialFullEmptyAndInvalidStates()
    {
        var viewModel = CreateViewModel(includeSecondItem: true);
        var lines = viewModel.Items.ToArray();

        lines[0].InputQuantity = 1m;
        Assert.Equal("Entregando 1 de 6 unidades", viewModel.SummaryText);
        Assert.Equal("#334155", viewModel.SummaryColor);

        lines[0].InputQuantity = 3m;
        lines[1].InputQuantity = 3m;
        Assert.Equal("Entregando 6 de 6 unidades · Retiro total", viewModel.SummaryText);
        Assert.Equal("#15803D", viewModel.SummaryColor);

        viewModel.ClearAllCommand.Execute(null);
        Assert.Equal("Ingresa al menos una cantidad para entregar", viewModel.SummaryText);
        Assert.Equal("#B45309", viewModel.SummaryColor);

        lines[0].InputQuantity = 4m;
        Assert.Equal("Revisa las cantidades marcadas", viewModel.SummaryText);
        Assert.Equal("#DC2626", viewModel.SummaryColor);
    }

    [Fact]
    public void NotesCounterAndInvoiceContext_UseTheExistingPickupData()
    {
        var pickup = new PendingPickupClientDto
        {
            SaleId = 9,
            InvoiceNumber = 12,
            CustomerName = "Ana Pérez",
            Items = new List<PendingPickupItemDto>
            {
                new() { SaleItemId = 101, ProductId = 1, ProductName = "Arroz", Quantity = 5m, PendingQuantity = 3m }
            }
        };

        var viewModel = new PartialDeliveryDialogViewModel(pickup)
        {
            Notes = new string('a', PartialDeliveryDialogViewModel.NotesMaxLength)
        };

        Assert.Equal("Factura N° 00012 · Ana Pérez", viewModel.InvoiceContextLabel);
        Assert.True(viewModel.HasInvoiceContext);
        Assert.Equal("300 / 300", viewModel.NotesCounterText);
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
                    Quantity = 5m,
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

    private static PartialDeliveryDialogViewModel CreateViewModel(
        bool includeSecondItem = false,
        decimal firstPending = 3m)
    {
        var items = new List<PendingPickupItemDto>
        {
            new()
            {
                SaleItemId = 101,
                ProductId = 1,
                ProductName = "Arroz",
                Quantity = firstPending + 2m,
                DeliveredQuantity = 2m,
                PendingQuantity = firstPending
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
                DeliveredQuantity = 1m,
                PendingQuantity = 3m
            });
        }

        return new PartialDeliveryDialogViewModel(new PendingPickupClientDto { Items = items });
    }
}
