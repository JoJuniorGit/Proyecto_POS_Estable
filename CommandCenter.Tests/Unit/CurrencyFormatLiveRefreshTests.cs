using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.143: el cambio del ajuste de formato de moneda (Venezolano/Internacional) debe refrescar en
// vivo las pantallas WPF abiertas vía mensajería (PropertyChanged inmediato, sin recomputo/rebindeo).
// La suite está serializada y cada prueba restaura CurrencyDisplay.Current en finally.
public class CurrencyFormatLiveRefreshTests
{
    private static Mock<IExchangeRateService> CreateRateMock(decimal rate)
    {
        var mock = new Mock<IExchangeRateService>();
        mock.Setup(e => e.CurrentRate).Returns(rate);
        return mock;
    }

    // 8.153 (SEC-06): el arqueo teórico solo se muestra a supervisores; la prueba de formateo
    // en vivo del saldo necesita una sesión Admin para seguir ejercitando esos displays.
    private static UserSession CreateAdminSession()
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Supervisor Test", Cedula = "V-1", Role = UserRole.Admin });
        return session;
    }

    [Fact]
    public void ProductItemViewModel_AlRecibirCambioDeFormato_RelanzaDisplays()
    {
        var previous = CurrencyDisplay.Current;
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;
        var product = new ProductItemViewModel(
            new ProductDto
            {
                Id = 950,
                Name = "Harina PAN",
                SKU = "HAR-950",
                IsActive = true,
                PriceUSD = 10.00m,
                PriceBsS = 0m
            },
            CreateRateMock(50.00m).Object);

        // InventoryViewModel es el receptor del mensaje y re-notifica cada fila (la fila no se
        // registra por sí misma). UserSession sin login evita el load inicial de fondo.
        var inventory = new InventoryViewModel(
            new Mock<IProductService>().Object,
            CreateRateMock(50.00m).Object,
            new UserSession());

        var changed = new List<string>();
        product.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        try
        {
            inventory.Products = new ObservableCollection<ProductItemViewModel> { product };
            Assert.Equal("Bs.S 500,00", product.DisplayRetailPrice);

            CurrencyDisplay.Current = MoneyDisplayFormat.International;
            WeakReferenceMessenger.Default.Send(new CurrencyFormatChangedMessage());

            Assert.Contains(nameof(ProductItemViewModel.DisplayRetailPrice), changed);
            Assert.Equal("Bs.S 500.00", product.DisplayRetailPrice);
        }
        finally
        {
            inventory.Dispose();
            CurrencyDisplay.Current = previous;
        }
    }

    [Fact]
    public async Task CashDrawerViewModel_AlRecibirCambioDeFormato_RelanzaFormateados()
    {
        var previous = CurrencyDisplay.Current;
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;

        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer.Setup(s => s.GetActiveSessionAsync())
            .ReturnsAsync(new CashDrawerSessionDto { Id = 1 });
        cashDrawer.Setup(s => s.GetHistoryAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<CashTransactionDto>());
        cashDrawer.Setup(s => s.GetCurrentBalanceLocalAsync(1)).ReturnsAsync(1300m);

        var vm = new CashDrawerViewModel(cashDrawer.Object, CreateRateMock(50.00m).Object, userSession: CreateAdminSession());
        try
        {
            await vm.LoadSessionAsync();

            Assert.Equal("1.300", vm.FormattedBalanceBsS);
            Assert.Equal("26,00 $", vm.FormattedBalanceUsd);

            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

            CurrencyDisplay.Current = MoneyDisplayFormat.International;
            WeakReferenceMessenger.Default.Send(new CurrencyFormatChangedMessage());

            Assert.Contains(nameof(CashDrawerViewModel.FormattedBalanceBsS), changed);
            Assert.Contains(nameof(CashDrawerViewModel.FormattedBalanceUsd), changed);
            Assert.Equal("1,300", vm.FormattedBalanceBsS);
            Assert.Equal("26.00 $", vm.FormattedBalanceUsd);
        }
        finally
        {
            vm.Dispose();
            CurrencyDisplay.Current = previous;
        }
    }

    [Fact]
    public void DailyClosureViewModel_AlRecibirCambioDeFormato_RelanzaDiferencias()
    {
        var previous = CurrencyDisplay.Current;
        CurrencyDisplay.Current = MoneyDisplayFormat.Venezuelan;

        var vm = new DailyClosureViewModel(
            new Mock<IDailyClosureClientService>().Object,
            new Mock<IDialogService>().Object);
        try
        {
            var row = new ClosureDetailRow(1, "Efectivo USD", 100m, () => { });
            vm.DetailRows.Add(row);
            row.ActualAmountBsS = 130m;
            vm.TotalDifferenceBsS = 30m;

            Assert.Equal("SOBRANTE EN CAJA (+30,00 Bs.S)", vm.DifferenceStatusLabel);
            Assert.Equal("+30,00", row.DifferenceDisplay);

            var rowChanged = new List<string>();
            var vmChanged = new List<string>();
            row.PropertyChanged += (_, e) => rowChanged.Add(e.PropertyName ?? string.Empty);
            vm.PropertyChanged += (_, e) => vmChanged.Add(e.PropertyName ?? string.Empty);

            CurrencyDisplay.Current = MoneyDisplayFormat.International;
            WeakReferenceMessenger.Default.Send(new CurrencyFormatChangedMessage());

            Assert.Contains(nameof(ClosureDetailRow.DifferenceDisplay), rowChanged);
            Assert.Contains(nameof(DailyClosureViewModel.DifferenceStatusLabel), vmChanged);
            Assert.Equal("SOBRANTE EN CAJA (+30.00 Bs.S)", vm.DifferenceStatusLabel);
            Assert.Equal("+30.00", row.DifferenceDisplay);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
            CurrencyDisplay.Current = previous;
        }
    }
}
