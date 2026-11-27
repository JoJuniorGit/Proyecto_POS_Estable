using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// RBAC de la barra de edición de precios del Inventario: los 4 comandos expuestos por los atajos
/// (Ctrl+N/E/A, Esc) deben respetar <c>CanMutateCatalog</c>, espejo del contrato del XAML de la
/// barra 2 y del guard runtime de <c>OpenBulkPriceEdit</c>. La sesión legacy (null) conserva el
/// comportamiento previo (permitido), igual que el guard existente.
/// </summary>
public class InventoryCommandPermissionTests
{
    private readonly Mock<IProductService> _productService = new();
    private readonly Mock<IExchangeRateService> _exchangeRate = new();
    private readonly Mock<IDialogService> _dialog = new();

    public InventoryCommandPermissionTests()
    {
        _exchangeRate.Setup(s => s.CurrentRate).Returns(36.5m);
        _productService
            .Setup(s => s.GetPagedAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>()))
            .ReturnsAsync(new PagedResultDto<ProductDto>
            {
                Items = new List<ProductDto>
                {
                    new() { Id = 1, Name = "Producto 1", SKU = "SKU-1", PriceUSD = 1m, PriceBsS = 36.5m, IsActive = true }
                },
                TotalCount = 1,
                HasMore = false
            });
    }

    private static UserSession CreateSession(UserRole role)
    {
        var session = new UserSession();
        session.CurrentUser = new UserDto { Id = 1, Name = role.ToString(), Role = role };
        return session;
    }

    private InventoryViewModel CreateViewModel(UserSession? session)
    {
        var vm = new InventoryViewModel(
            _productService.Object,
            _exchangeRate.Object,
            session,
            _dialog.Object);

        // Aísla del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        return vm;
    }

    [Fact]
    public async Task Cashier_WithActiveSelection_CannotExecuteAnyCatalogMutationCommand()
    {
        var session = CreateSession(UserRole.Admin);
        var vm = CreateViewModel(session);
        await WaitUntilAsync(() => vm.Products.Count == 1);
        vm.Products[0].IsSelected = true;
        Assert.True(vm.HasSelection);

        session.CurrentUser = new UserDto { Id = 2, Name = "Cajero", Role = UserRole.Cashier };

        Assert.False(vm.OpenAddProductCommand.CanExecute(null));
        Assert.False(vm.SelectAllFilteredCommand.CanExecute(null));
        Assert.False(vm.ClearSelectionCommand.CanExecute(null));
        Assert.False(vm.OpenBulkPriceEditCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Manager)]
    public async Task MutatorRoles_EnableCatalogMutationCommands(UserRole role)
    {
        var vm = CreateViewModel(CreateSession(role));
        await WaitUntilAsync(() => vm.Products.Count == 1);

        Assert.True(vm.OpenAddProductCommand.CanExecute(null));
        Assert.True(vm.SelectAllFilteredCommand.CanExecute(null));
        Assert.True(vm.ClearSelectionCommand.CanExecute(null));
        Assert.False(vm.OpenBulkPriceEditCommand.CanExecute(null)); // Sin selección: el comando manda.

        vm.Products[0].IsSelected = true;

        Assert.True(vm.OpenBulkPriceEditCommand.CanExecute(null));
    }

    [Fact]
    public void SessionWithoutUser_DisablesCatalogMutationCommands()
    {
        var vm = CreateViewModel(new UserSession());

        Assert.False(vm.OpenAddProductCommand.CanExecute(null));
        Assert.False(vm.SelectAllFilteredCommand.CanExecute(null));
        Assert.False(vm.ClearSelectionCommand.CanExecute(null));
        Assert.False(vm.OpenBulkPriceEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task LegacyNullSession_KeepsCatalogMutationCommandsEnabled()
    {
        var vm = CreateViewModel(null);
        await WaitUntilAsync(() => vm.Products.Count == 1);
        vm.Products[0].IsSelected = true;

        Assert.True(vm.OpenAddProductCommand.CanExecute(null));
        Assert.True(vm.SelectAllFilteredCommand.CanExecute(null));
        Assert.True(vm.ClearSelectionCommand.CanExecute(null));
        Assert.True(vm.OpenBulkPriceEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task LiveSessionChange_FromCashierToAdmin_ReenablesCommands()
    {
        var session = CreateSession(UserRole.Cashier);
        var vm = CreateViewModel(session);
        await WaitUntilAsync(() => vm.Products.Count == 1);
        vm.Products[0].IsSelected = true;

        Assert.False(vm.OpenAddProductCommand.CanExecute(null));
        Assert.False(vm.SelectAllFilteredCommand.CanExecute(null));
        Assert.False(vm.ClearSelectionCommand.CanExecute(null));
        Assert.False(vm.OpenBulkPriceEditCommand.CanExecute(null));

        session.CurrentUser = new UserDto { Id = 3, Name = "Admin", Role = UserRole.Admin };

        Assert.True(vm.OpenAddProductCommand.CanExecute(null));
        Assert.True(vm.SelectAllFilteredCommand.CanExecute(null));
        Assert.True(vm.ClearSelectionCommand.CanExecute(null));
        Assert.True(vm.OpenBulkPriceEditCommand.CanExecute(null));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "La condición esperada no se cumplió dentro del tiempo límite.");
    }
}
