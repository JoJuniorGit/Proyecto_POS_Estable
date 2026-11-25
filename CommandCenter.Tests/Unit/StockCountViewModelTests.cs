using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// RBAC and blind-flow tests for the WPF physical stock count. Cashier can count but never sees
/// the theoretical/cost review; Driver has no access at all; Admin/Manager own the lifecycle.
/// </summary>
public sealed class StockCountViewModelTests
{
    private readonly Mock<IStockCountClientService> _service = new();
    private readonly Mock<ICategoryService> _categories = new();
    private readonly Mock<IDialogService> _dialogs = new();

    public StockCountViewModelTests()
    {
        SetupDefaults();
    }

    private void SetupDefaults()
    {
        _service
            .Setup(service => service.ListAsync(It.IsAny<StockCountStatus?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StockCountSessionDto> { CreateSession(1, StockCountStatus.Open, itemsCount: 1) });
        _service
            .Setup(service => service.GetDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => new StockCountDetailDto(
                CreateSession(id, StockCountStatus.Open, itemsCount: 1),
                new List<StockCountItemDto> { CreateItem() }));
        _service
            .Setup(service => service.GetReviewAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StockCountReviewDto(
                1,
                StockCountStatus.Open,
                TheoreticalIsPreview: true,
                Items: new List<StockCountReviewItemDto>(),
                TotalSurplusQuantity: 0m,
                TotalShortageQuantity: 0m,
                TotalSurplusValueUsd: 0m,
                TotalShortageValueUsd: 0m,
                NetValueUsd: 0m,
                UnscannedInScopeCount: 0));
        _service
            .Setup(service => service.ScanAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateItem());
        _service
            .Setup(service => service.SetCountedQuantityAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateItem());
        _service
            .Setup(service => service.CreateAsync(It.IsAny<CreateStockCountRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSession(2, StockCountStatus.Open));
        _service
            .Setup(service => service.CloseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSession(1, StockCountStatus.Closed));
        _service
            .Setup(service => service.ApplyAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSession(1, StockCountStatus.Applied));
        _service
            .Setup(service => service.CancelAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSession(1, StockCountStatus.Cancelled));
        _service
            .Setup(service => service.RemoveItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _categories
            .Setup(service => service.GetCategoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CategoryDto>
            {
                new(1, "Alimentos", null, 0, "Alimentos")
            });
        _dialogs
            .Setup(dialog => dialog.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        _dialogs
            .Setup(dialog => dialog.ShowTextInputAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("5");
    }

    private StockCountViewModel CreateViewModel(UserRole? role = null, IProductService? productService = null)
    {
        UserSession? session = null;
        if (role.HasValue)
        {
            session = new UserSession
            {
                CurrentUser = new UserDto { Id = 1, Name = "Usuario Test", Role = role.Value }
            };
        }

        return new StockCountViewModel(
            _service.Object,
            _categories.Object,
            _dialogs.Object,
            session,
            productService: productService);
    }

    private static StockCountSessionDto CreateSession(int id, StockCountStatus status, int itemsCount = 0)
        => new(
            id,
            $"Sesión {id}",
            status,
            ScopeCategoryId: null,
            ScopeCategoryPath: null,
            ItemsCount: itemsCount,
            CreatedByUserId: "user-1",
            CreatedAt: new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc),
            ClosedAt: status is StockCountStatus.Closed or StockCountStatus.Applied
                ? new DateTime(2026, 11, 8, 13, 0, 0, DateTimeKind.Utc)
                : null,
            AppliedAt: status == StockCountStatus.Applied
                ? new DateTime(2026, 11, 8, 13, 5, 0, DateTimeKind.Utc)
                : null);

    private static StockCountItemDto CreateItem()
        => new(10, "SKU-1", "Producto de prueba", 3m, new DateTime(2026, 11, 8, 12, 30, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Driver_HasNoAccess_AndDoesNotRequestSessions()
    {
        var vm = CreateViewModel(UserRole.Driver);

        Assert.False(vm.HasAccess);
        Assert.False(vm.CanManage);
        Assert.False(vm.CanCount);

        await vm.EnsureLoadedAsync();

        Assert.Equal(StockCountViewModel.AccessDeniedMessage, vm.ErrorMessage);
        _service.Verify(
            service => service.ListAsync(It.IsAny<StockCountStatus?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Cashier_CanCount_ButNeverLoadsTheReview()
    {
        var vm = CreateViewModel(UserRole.Cashier);
        await vm.EnsureLoadedAsync();

        Assert.True(vm.HasAccess);
        Assert.True(vm.CanCount);
        Assert.False(vm.CanManage);
        Assert.False(vm.CanCreateSession);

        var row = Assert.Single(vm.Sessions);
        await vm.SelectSessionCommand.ExecuteAsync(row);

        _service.Verify(service => service.GetDetailAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _service.Verify(service => service.GetReviewAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(vm.Review);
        Assert.False(vm.ShowReview);
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task Manager_SelectingSession_LoadsDetailAndReview()
    {
        var vm = CreateViewModel(UserRole.Manager);
        await vm.EnsureLoadedAsync();

        await vm.SelectSessionCommand.ExecuteAsync(vm.Sessions.Single());

        Assert.NotNull(vm.Review);
        Assert.True(vm.ShowReview);
        Assert.True(vm.Review!.TheoreticalIsPreview);
        _service.Verify(service => service.GetReviewAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cashier_CloseApplyCancelCommands_AreNoOps()
    {
        var vm = CreateViewModel(UserRole.Cashier);
        await vm.EnsureLoadedAsync();
        await vm.SelectSessionCommand.ExecuteAsync(vm.Sessions.Single());

        await vm.CloseCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);
        await vm.CancelCommand.ExecuteAsync(null);

        _service.Verify(service => service.CloseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.Verify(service => service.ApplyAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.Verify(service => service.CancelAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Scan_OpenSession_ClearsInputAndReloadsDetail()
    {
        var vm = CreateViewModel(UserRole.Cashier);
        await vm.EnsureLoadedAsync();
        await vm.SelectSessionCommand.ExecuteAsync(vm.Sessions.Single());

        vm.ScanCode = "SKU-1";
        await vm.ScanCommand.ExecuteAsync(null);

        _service.Verify(
            service => service.ScanAsync(1, "SKU-1", 1m, It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(string.Empty, vm.ScanCode);
        Assert.Contains("Producto de prueba", vm.ScanFeedback, StringComparison.Ordinal);
        Assert.False(vm.ScanFeedbackIsError);
    }

    [Fact]
    public async Task Create_EmptyName_DoesNotCallTheService()
    {
        var vm = CreateViewModel(UserRole.Admin);
        await vm.EnsureLoadedAsync();

        vm.OpenCreatePanelCommand.Execute(null);
        vm.CreateName = "   ";
        await vm.SubmitCreateCommand.ExecuteAsync(null);

        Assert.Equal("El nombre de la sesión es obligatorio.", vm.CreateError);
        _service.Verify(
            service => service.CreateAsync(It.IsAny<CreateStockCountRequestDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_ValidPayload_ClosesPanelAndReloadsSessions()
    {
        _service
            .Setup(service => service.ListAsync(It.IsAny<StockCountStatus?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StockCountSessionDto>
            {
                CreateSession(1, StockCountStatus.Open, itemsCount: 1),
                CreateSession(2, StockCountStatus.Open)
            });
        var vm = CreateViewModel(UserRole.Admin);
        await vm.EnsureLoadedAsync();

        vm.OpenCreatePanelCommand.Execute(null);
        vm.CreateName = "Conteo noviembre";
        vm.CreateNotes = "Notas de prueba";
        await vm.SubmitCreateCommand.ExecuteAsync(null);

        Assert.False(vm.IsCreatePanelOpen);
        _service.Verify(
            service => service.CreateAsync(
                It.Is<CreateStockCountRequestDto>(request =>
                    request.Name == "Conteo noviembre" &&
                    request.Notes == "Notas de prueba"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Close_DeclinedConfirmation_DoesNotCallTheService()
    {
        _dialogs
            .Setup(dialog => dialog.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);
        var vm = CreateViewModel(UserRole.Manager);
        await vm.EnsureLoadedAsync();
        await vm.SelectSessionCommand.ExecuteAsync(vm.Sessions.Single());

        await vm.CloseCommand.ExecuteAsync(null);

        _service.Verify(service => service.CloseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Close_Confirmed_CallsServiceAndRefreshesStatus()
    {
        var currentStatus = StockCountStatus.Open;
        _service
            .Setup(service => service.ListAsync(It.IsAny<StockCountStatus?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<StockCountSessionDto>
            {
                CreateSession(1, currentStatus, itemsCount: 1)
            });
        _service
            .Setup(service => service.GetDetailAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new StockCountDetailDto(
                CreateSession(1, currentStatus, itemsCount: 1),
                new List<StockCountItemDto> { CreateItem() }));
        _service
            .Setup(service => service.CloseAsync(1, It.IsAny<CancellationToken>()))
            .Callback(() => currentStatus = StockCountStatus.Closed)
            .ReturnsAsync(CreateSession(1, StockCountStatus.Closed));
        var vm = CreateViewModel(UserRole.Manager);
        await vm.EnsureLoadedAsync();
        await vm.SelectSessionCommand.ExecuteAsync(vm.Sessions.Single());

        await vm.CloseCommand.ExecuteAsync(null);

        _service.Verify(service => service.CloseAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(vm.SelectedSession);
        Assert.Equal(StockCountStatus.Closed, vm.SelectedSession!.Status);
        Assert.True(vm.CanApply);
        Assert.False(vm.CanClose);
    }

    [Fact]
    public async Task EditItem_UsesDialogAndSetsAbsoluteQuantity()
    {
        var vm = CreateViewModel(UserRole.Cashier);
        await vm.EnsureLoadedAsync();
        await vm.SelectSessionCommand.ExecuteAsync(vm.Sessions.Single());

        await vm.EditItemCommand.ExecuteAsync(vm.Items.Single());

        _dialogs.Verify(
            dialog => dialog.ShowTextInputAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
        _service.Verify(
            service => service.SetCountedQuantityAsync(1, 10, 5m, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RemoveItem_Confirmed_CallsService()
    {
        var vm = CreateViewModel(UserRole.Cashier);
        await vm.EnsureLoadedAsync();
        await vm.SelectSessionCommand.ExecuteAsync(vm.Sessions.Single());

        await vm.RemoveItemCommand.ExecuteAsync(vm.Items.Single());

        _service.Verify(
            service => service.RemoveItemAsync(1, 10, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SessionSearch_FiltersSessionsByName()
    {
        _service
            .Setup(service => service.ListAsync(It.IsAny<StockCountStatus?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StockCountSessionDto>
            {
                CreateSession(1, StockCountStatus.Open),
                CreateSession(2, StockCountStatus.Open)
            });
        var vm = CreateViewModel(UserRole.Admin);
        await vm.EnsureLoadedAsync();

        Assert.Equal(2, vm.FilteredSessions.Count);

        vm.SessionSearchText = "Sesión 2";

        var row = Assert.Single(vm.FilteredSessions);
        Assert.Equal(2, row.Id);
        Assert.False(vm.HasNoVisibleSessions);
        Assert.False(vm.HasNoSearchResults);

        vm.SessionSearchText = string.Empty;

        Assert.Equal(2, vm.FilteredSessions.Count);
    }

    [Fact]
    public async Task SessionSearch_NoMatches_DoesNotLookLikeAnEmptyList()
    {
        var vm = CreateViewModel(UserRole.Admin);
        await vm.EnsureLoadedAsync();

        vm.SessionSearchText = "no-existe";

        Assert.Empty(vm.FilteredSessions);
        Assert.True(vm.HasNoVisibleSessions);
        Assert.True(vm.HasNoSearchResults);
        Assert.False(vm.HasNoSessions);
    }

    [Fact]
    public void CreatePanel_StartsCollapsed_AndHidesTheOpenCta()
    {
        var vm = CreateViewModel(UserRole.Admin);

        Assert.False(vm.IsCreatePanelOpen);
        Assert.True(vm.CanOpenCreatePanel);
        Assert.Equal("0 / 300", vm.CreateNotesCounterText);

        vm.CreateNotes = "abc";
        Assert.Equal("3 / 300", vm.CreateNotesCounterText);

        vm.OpenCreatePanelCommand.Execute(null);

        Assert.True(vm.IsCreatePanelOpen);
        Assert.False(vm.CanOpenCreatePanel);

        vm.CancelCreateCommand.Execute(null);

        Assert.False(vm.IsCreatePanelOpen);
        Assert.True(vm.CanOpenCreatePanel);
    }

    [Fact]
    public void Cashier_CannotOpenTheCreatePanel()
    {
        var vm = CreateViewModel(UserRole.Cashier);

        Assert.False(vm.CanOpenCreatePanel);
    }

    [Fact]
    public async Task SelectingSession_ClosesTheCreatePanel()
    {
        var vm = CreateViewModel(UserRole.Admin);
        await vm.EnsureLoadedAsync();
        vm.OpenCreatePanelCommand.Execute(null);

        await vm.SelectSessionCommand.ExecuteAsync(vm.Sessions.Single());

        Assert.False(vm.IsCreatePanelOpen);
        Assert.True(vm.HasSelectedSession);
    }

    [Fact]
    public void ScopeTypeOptions_ExposeAllCategoryAndProducts()
    {
        var vm = CreateViewModel(UserRole.Admin);

        Assert.Equal(
            new[] { StockCountScopeType.All, StockCountScopeType.Category, StockCountScopeType.Products },
            vm.ScopeTypeOptions.Select(option => option.Value).ToArray());
        Assert.Equal(StockCountScopeType.All, vm.SelectedCreateScopeType.Value);
        Assert.True(vm.IsCreateScopeValid);
    }

    [Fact]
    public void ProductScope_RequiresAtLeastOneSelectedProduct()
    {
        var vm = CreateViewModel(UserRole.Admin);
        vm.OpenCreatePanelCommand.Execute(null);
        vm.SelectedCreateScopeType = vm.ScopeTypeOptions.Single(option => option.Value == StockCountScopeType.Products);

        Assert.False(vm.HasCreateScopeProducts);
        Assert.False(vm.IsCreateScopeValid);
        Assert.False(vm.CanCreateSession);
        Assert.Equal("0 / 1000", vm.CreateScopeProductCountText);

        vm.CreateScopeProducts.Add(new ProductPickerSelection(10, "SKU-1", "Harina"));

        Assert.True(vm.HasCreateScopeProducts);
        Assert.True(vm.IsCreateScopeValid);
        Assert.True(vm.CanCreateSession);
        Assert.Equal("1 / 1000", vm.CreateScopeProductCountText);
    }

    [Fact]
    public void ProductPickerCommand_TransfersSelectionIntoTheCreateForm()
    {
        var productService = new Mock<IProductService>();
        var vm = CreateViewModel(UserRole.Admin, productService.Object);
        vm.OpenCreatePanelCommand.Execute(null);
        vm.SelectedCreateScopeType = vm.ScopeTypeOptions.Single(option => option.Value == StockCountScopeType.Products);

        _dialogs
            .Setup(dialog => dialog.ShowProductPickerDialog(It.IsAny<ProductPickerDialogViewModel>()))
            .Callback<ProductPickerDialogViewModel>(picker => picker.SelectedProducts.Add(
                new ProductPickerSelection(10, "SKU-1", "Harina")))
            .Returns(true);

        vm.OpenCreateScopeProductPickerCommand.Execute(null);

        var selected = Assert.Single(vm.CreateScopeProducts);
        Assert.Equal(10, selected.Id);
        Assert.Equal("1 / 1000", vm.CreateScopeProductCountText);
    }

    [Fact]
    public async Task Create_ProductScope_SendsScopeTypeAndSelectedIds()
    {
        var productService = new Mock<IProductService>();
        var vm = CreateViewModel(UserRole.Admin, productService.Object);
        await vm.EnsureLoadedAsync();
        vm.OpenCreatePanelCommand.Execute(null);
        vm.SelectedCreateScopeType = vm.ScopeTypeOptions.Single(option => option.Value == StockCountScopeType.Products);
        vm.CreateScopeProducts.Add(new ProductPickerSelection(10, "SKU-1", "Harina"));
        vm.CreateName = "Conteo focalizado";

        await vm.SubmitCreateCommand.ExecuteAsync(null);

        _service.Verify(service => service.CreateAsync(
            It.Is<CreateStockCountRequestDto>(request =>
                request.ScopeType == StockCountScopeType.Products &&
                request.ScopeCategoryId == null &&
                request.ScopeProductIds != null &&
                request.ScopeProductIds.SequenceEqual(new[] { 10 })),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Scan_OutOfScope409_FlagsAmberScopeWarning()
    {
        _service
            .Setup(service => service.ScanAsync(1, "SKU-OUT", It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "El producto 'Fuera' (SKU SKU-OUT) no pertenece a los productos seleccionados de la sesión de conteo #1."));
        var vm = CreateViewModel(UserRole.Cashier);
        await vm.EnsureLoadedAsync();
        await vm.SelectSessionCommand.ExecuteAsync(vm.Sessions.Single());
        vm.ScanCode = "SKU-OUT";

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.True(vm.ScanFeedbackIsError);
        Assert.True(vm.ScanFeedbackIsScopeViolation);
        Assert.Equal("#B45309", vm.ScanFeedbackColor);
        Assert.Contains("productos seleccionados", vm.ScanFeedback);
    }
}
