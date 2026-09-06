using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PosViewModelPaymentMethodsDeduplicationTests
{
    private static List<PaymentMethodDto> CreateSamplePaymentMethods() => new()
    {
        new PaymentMethodDto { Id = 1, Name = "Efectivo", IsCash = true, DisplayOrder = 1 },
        new PaymentMethodDto { Id = 2, Name = "Divisas", IsCash = false, DisplayOrder = 2 },
        new PaymentMethodDto { Id = 3, Name = "Transferencia BBVA", IsCash = false, DisplayOrder = 3 },
        new PaymentMethodDto { Id = 4, Name = "BioPago", IsCash = false, DisplayOrder = 4 },
        new PaymentMethodDto { Id = 5, Name = "Tarjeta", IsCash = false, DisplayOrder = 5 }
    };

    [Fact]
    public async Task PosViewModel_InitializeForSessionAsync_ConcurrentCalls_LoadsMethodsOnlyOnce()
    {
        // Arrange
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var mockDialog = new Mock<IDialogService>();

        mockRate.SetupGet(r => r.CurrentRate).Returns(60.0m);
        mockSales.Setup(s => s.StartSaleAsync(It.IsAny<int?>()))
            .ReturnsAsync(new SaleDto { Id = 101, Status = "Draft" });

        mockPayments.Setup(p => p.GetActiveMethodsAsync())
            .Returns(async () =>
            {
                await Task.Delay(50); // Simula latencia de red para forzar carrera
                return CreateSamplePaymentMethods();
            });

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }, "test-token");

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        using var posVm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session,
            mockDialog.Object);

        // Act: Dos llamadas concurrentes simulando SessionChanged + LoginSuccess
        var task1 = posVm.InitializeForSessionAsync();
        var task2 = posVm.InitializeForSessionAsync();
        await Task.WhenAll(task1, task2);

        // Assert: Se debió invocar exactamente una vez y no haber métodos duplicados
        mockPayments.Verify(p => p.GetActiveMethodsAsync(), Times.Once);
        Assert.Equal(5, posVm.ActivePaymentMethods.Count);
        Assert.Equal(5, posVm.ActivePaymentMethods.Select(m => m.Id).Distinct().Count());
        mockSales.Verify(s => s.StartSaleAsync(It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public async Task LoadPaymentMethodsAsync_CleansAndPreventsDuplicates()
    {
        // Arrange
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();

        var sampleMethodsWithDuplicates = new List<PaymentMethodDto>
        {
            new PaymentMethodDto { Id = 1, Name = "Efectivo", IsCash = true },
            new PaymentMethodDto { Id = 2, Name = "Divisas", IsCash = false },
            new PaymentMethodDto { Id = 1, Name = "Efectivo Duplicado", IsCash = true }, // Duplicado de ID 1
            new PaymentMethodDto { Id = 3, Name = "Tarjeta", IsCash = false }
        };

        mockPayments.Setup(p => p.GetActiveMethodsAsync())
            .ReturnsAsync(sampleMethodsWithDuplicates);

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }, "token");

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        using var posVm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session);

        // Pre-poblar con elementos antiguos
        posVm.ActivePaymentMethods.Add(new PaymentMethodDto { Id = 99, Name = "Metodo Viejo" });

        // Act
        await posVm.ReloadPaymentMethodsAsync();

        // Assert: El método viejo se limpió y el duplicado con Id = 1 fue rechazado
        Assert.Equal(3, posVm.ActivePaymentMethods.Count);
        Assert.DoesNotContain(posVm.ActivePaymentMethods, m => m.Id == 99);
        Assert.Single(posVm.ActivePaymentMethods, m => m.Id == 1);
        Assert.Single(posVm.ActivePaymentMethods, m => m.Id == 2);
        Assert.Single(posVm.ActivePaymentMethods, m => m.Id == 3);
    }

    [Fact]
    public void ResetSession_ClearsActivePaymentMethodsAndPosState()
    {
        // Arrange
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();

        var session = new UserSession();
        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        cartVm.CurrentSale = new SaleDto { Id = 55 };

        using var posVm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session);

        posVm.ActivePaymentMethods.Add(new PaymentMethodDto { Id = 1, Name = "Efectivo" });
        posVm.ActivePaymentMethods.Add(new PaymentMethodDto { Id = 2, Name = "Divisas" });
        posVm.SearchText = "Arroz";
        posVm.Suggestions.Add(new ProductQuickInfoDto { Id = 1, Name = "Arroz 1kg" });

        // Act
        posVm.ResetSession();

        // Assert
        Assert.Empty(posVm.ActivePaymentMethods);
        Assert.Null(posVm.Cart.CurrentSale);
        Assert.Empty(posVm.Cart.CartItems);
        Assert.Empty(posVm.RecentScannedProducts);
        Assert.Empty(posVm.SearchText);
        Assert.Empty(posVm.Suggestions);
    }

    [Fact]
    public async Task MainViewModel_LoginSuccess_NavigatesOnlyOnce()
    {
        // Arrange
        var userSession = new UserSession();
        var mockUserService = new Mock<IUserService>();
        var mockSalesService = new Mock<ISalesService>();
        var mockProductService = new Mock<IProductService>();
        var mockPaymentService = new Mock<IPaymentService>();
        var mockRateService = new Mock<IExchangeRateService>();
        var mockDialogService = new Mock<IDialogService>();

        mockRateService.SetupGet(r => r.CurrentRate).Returns(60.0m);
        mockPaymentService.Setup(p => p.GetActiveMethodsAsync())
            .ReturnsAsync(CreateSamplePaymentMethods());
        mockSalesService.Setup(s => s.StartSaleAsync(It.IsAny<int?>()))
            .ReturnsAsync(new SaleDto { Id = 200, Status = "Draft" });

        var cartVm = new CartViewModel(mockSalesService.Object, mockRateService.Object);
        using var posVm = new PosViewModel(
            mockSalesService.Object,
            mockProductService.Object,
            mockPaymentService.Object,
            mockRateService.Object,
            cartVm,
            userSession,
            mockDialogService.Object);

        mockUserService.Setup(u => u.LoginAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new LoginResultDto
            {
                Token = "jwt-token",
                User = new UserDto { Id = 10, Name = "Maria", Role = UserRole.Cashier }
            });

        var loginVm = new LoginViewModel(mockUserService.Object, mockDialogService.Object, userSession);

        var mainVm = new MainViewModel(
            userSession,
            loginVm,
            posVm,
            null, null, null, null, null, null, null, null, null, null,
            null,
            mockDialogService.Object,
            mockRateService.Object);

        Assert.Equal(loginVm, mainVm.CurrentViewModel);

        // Act: Ejecutar Login
        loginVm.Cedula = "V12345678";
        loginVm.Password = "Password123!";
        await loginVm.LoginCommand.ExecuteAsync(null);

        // Assert: CurrentViewModel debe ser posVm
        Assert.Equal(posVm, mainVm.CurrentViewModel);

        // Esperar cualquier inicialización asíncrona pendiente
        await Task.Delay(100);

        // ActivePaymentMethods debe tener exactamente 5 elementos sin duplicación
        Assert.Equal(5, posVm.ActivePaymentMethods.Count);
        mockPaymentService.Verify(p => p.GetActiveMethodsAsync(), Times.Once);
        mockSalesService.Verify(s => s.StartSaleAsync(It.IsAny<int?>()), Times.Once);
    }
}
