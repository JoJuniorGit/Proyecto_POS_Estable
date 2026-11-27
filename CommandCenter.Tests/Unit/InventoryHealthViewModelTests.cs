using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 15 (S5/S6): el view model no duplica el motor de precios; solo pide los IDs de stock
/// muerto y abre el dialogo del Paso 9 con una seleccion explicita.
/// </summary>
public class InventoryHealthViewModelTests
{
    private static InventoryRotationReportDto BuildReport(int deadStockCount = 1) => new()
    {
        GeneratedAtUtc = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc),
        FromUtc = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc),
        ToUtc = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc),
        SalesWindowDays = 90,
        DeadStockDays = 60,
        Summary = new InventoryRotationSummaryDto
        {
            TotalProducts = 4,
            HighRotationCount = 1,
            MediumRotationCount = 1,
            LowRotationCount = 4 - deadStockCount - 2 < 0 ? 0 : 4 - deadStockCount - 2,
            DeadStockCount = deadStockCount,
            TotalInventoryValueUsd = 400m,
            ImmobilizedCapitalUsd = 100m,
            ImmobilizedCapitalPercent = 25m
        },
        Products = new[]
        {
            new InventoryRotationProductDto
            {
                ProductId = 2,
                Name = "Queso",
                SKU = "QUESO-2",
                CategoryName = "Lacteos",
                StockQuantity = 10m,
                InventoryValueUsd = 100m,
                DaysSinceLastSale = 90,
                RotationClass = InventoryRotationClass.DeadStock
            }
        },
        Categories = Array.Empty<InventoryRotationCategoryDto>(),
        Page = 1,
        PageSize = 50,
        TotalCount = 1,
        TotalPages = 1
    };

    private static InventoryHealthViewModel CreateVm(
        Mock<IInventoryRotationService> rotation,
        Mock<IDialogService>? dialog = null,
        UserSession? session = null)
    {
        rotation
            .Setup(service => service.GetReportAsync(It.IsAny<InventoryRotationFilterDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildReport());

        return new InventoryHealthViewModel(
            rotation.Object,
            new Mock<IProductService>().Object,
            categoryService: null,
            dialogService: (dialog ?? new Mock<IDialogService>()).Object,
            settingsService: null,
            userSession: session);
    }

    [Fact]
    public async Task LoadAsync_AppliesSummaryAndBuildsFilters()
    {
        var rotation = new Mock<IInventoryRotationService>();
        var vm = CreateVm(rotation);
        vm.SalesWindowDays = 60;
        vm.DeadStockDays = 45;
        vm.SelectedCategoryFilter = new CategoryOption(7, "Lacteos", null, 0, "Lacteos");
        vm.SelectedRotationClassOption = vm.RotationClassOptions.Single(option => option.Value == "DeadStock");
        vm.SearchText = "queso";

        await vm.LoadAsync();

        Assert.Equal(1, vm.Summary.DeadStockCount);
        Assert.True(vm.HasDeadStock);
        Assert.Single(vm.Products);
        Assert.Equal("Pagina 1 de 1 (1 productos)", vm.PageSummary);

        rotation.Verify(service => service.GetReportAsync(
            It.Is<InventoryRotationFilterDto>(filter =>
                filter.SalesWindowDays == 60
                && filter.DeadStockDays == 45
                && filter.CategoryId == 7
                && filter.SearchText == "queso"
                && filter.RotationClass == InventoryRotationClass.DeadStock),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyPromotionAsync_OpensBulkPriceDialogWithDeadStockIds()
    {
        var rotation = new Mock<IInventoryRotationService>();
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(service => service.ShowBulkPriceEditDialog(It.IsAny<BulkPriceEditDialogViewModel>()))
            .Returns(false);

        rotation
            .Setup(service => service.GetDeadStockProductIdsAsync(
                It.IsAny<InventoryRotationFilterDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeadStockSelectionDto
            {
                ProductIds = new[] { 2, 9 },
                TotalCount = 2,
                Truncated = false
            });

        var vm = CreateVm(rotation, dialog);
        await vm.LoadAsync();
        await vm.ApplyPromotionCommand.ExecuteAsync(null);

        dialog.Verify(service => service.ShowBulkPriceEditDialog(
            It.Is<BulkPriceEditDialogViewModel>(dialogVm =>
                dialogVm.SelectedCount == 2 &&
                !dialogVm.IsSelectAllFiltered)),
            Times.Once);
        dialog.Verify(service => service.ShowSuccessDialog(It.IsAny<string>(), null), Times.Never);
        Assert.False(vm.HasAppliedPriceChanges);
    }

    [Fact]
    public async Task ApplyPromotionAsync_BlocksTruncatedSelection()
    {
        var rotation = new Mock<IInventoryRotationService>();
        var dialog = new Mock<IDialogService>();
        rotation
            .Setup(service => service.GetDeadStockProductIdsAsync(
                It.IsAny<InventoryRotationFilterDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeadStockSelectionDto
            {
                ProductIds = Enumerable.Range(1, 1000).ToArray(),
                TotalCount = 1005,
                Truncated = true
            });

        var vm = CreateVm(rotation, dialog);
        await vm.LoadAsync();
        await vm.ApplyPromotionCommand.ExecuteAsync(null);

        dialog.Verify(service => service.ShowWarning(
            "Demasiados productos",
            It.Is<string>(message => message.Contains("1005"))), Times.Once);
        dialog.Verify(service => service.ShowBulkPriceEditDialog(It.IsAny<BulkPriceEditDialogViewModel>()), Times.Never);
    }

    [Fact]
    public async Task CashierCannotApplyPromotion()
    {
        var session = new UserSession();
        session.SetUser(new UserDto
        {
            Id = 1,
            Name = "Cajero",
            Cedula = "V-1",
            Role = UserRole.Cashier
        });

        var rotation = new Mock<IInventoryRotationService>();
        var vm = CreateVm(rotation, session: session);
        await vm.LoadAsync();

        Assert.False(vm.CanMutateCatalog);
        Assert.False(vm.CanApplyPromotion);
        Assert.False(vm.ApplyPromotionCommand.CanExecute(null));
    }
}
