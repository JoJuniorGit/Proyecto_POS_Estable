using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests;

public class CashDrawerPhysicalMethodUiTests
{
    [Fact]
    public async Task LoadSessionAsync_PopulatesPhysicalMethodBalancesFromActiveSession()
    {
        var session = new CashDrawerSessionDto
        {
            Id = 42,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 1000m,
            OpeningBalanceUsd = 20m,
            OpeningExchangeRate = 50m,
            PhysicalMethodBalances = new List<PhysicalMethodBalanceClientDto>
            {
                new()
                {
                    PaymentMethodId = 10,
                    Name = "Caja Bs.S",
                    CurrencyCode = "VES",
                    CurrencySymbol = "Bs.S",
                    CurrentBalance = 1500m,
                    IsActive = true,
                    CanDispense = true
                },
                new()
                {
                    PaymentMethodId = 11,
                    Name = "Caja USD",
                    CurrencyCode = "USD",
                    CurrencySymbol = "$",
                    CurrentBalance = 25m,
                    IsActive = true,
                    CanDispense = true
                }
            }
        };

        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer.Setup(service => service.GetActiveSessionAsync()).ReturnsAsync(session);
        cashDrawer.Setup(service => service.GetHistoryAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<CashTransactionDto>());
        cashDrawer.Setup(service => service.GetCurrentBalanceLocalAsync(42)).ReturnsAsync(1500m);

        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(service => service.CurrentRate).Returns(50m);

        var userSession = new UserSession();
        using var viewModel = new CashDrawerViewModel(cashDrawer.Object, rate.Object, userSession: userSession);
        userSession.SetUser(new UserDto { Id = 1, Name = "Supervisor", Cedula = "V-1", Role = UserRole.Admin });

        await viewModel.LoadSessionAsync();

        Assert.True(viewModel.HasPhysicalMethodBalances);
        Assert.Equal(2, viewModel.PhysicalMethodBalances.Count);
        Assert.Equal("Caja USD", viewModel.PhysicalMethodBalances[1].Name);
        Assert.Equal(25m, viewModel.PhysicalMethodBalances[1].CurrentBalance);
    }
}
