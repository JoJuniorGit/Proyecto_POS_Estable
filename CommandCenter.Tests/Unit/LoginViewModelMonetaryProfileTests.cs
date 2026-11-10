using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class LoginViewModelMonetaryProfileTests
{
    [Fact]
    public async Task LoginAsync_Success_RefreshesMonetaryProfileDisplay()
    {
        var userService = new Mock<IUserService>();
        userService.Setup(u => u.LoginAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new LoginResultDto
            {
                Token = "jwt-token",
                User = new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }
            });

        var profileService = new Mock<IClientMonetaryProfileService>();
        profileService.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "EUR",
                PrimaryCurrencySymbol = "€",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null
            });

        var dialogService = new Mock<IDialogService>();
        var viewModel = new LoginViewModel(
            userService.Object,
            dialogService.Object,
            new UserSession(),
            monetaryProfileService: profileService.Object)
        {
            Cedula = "cajero",
            Password = "clave"
        };

        try
        {
            await viewModel.LoginCommand.ExecuteAsync(null);

            Assert.True(CurrencyDisplay.IsSingle);
            Assert.Equal("EUR", CurrencyDisplay.PrimaryCurrencyCode);
            Assert.Null(CurrencyDisplay.SecondaryCurrencyCode);
        }
        finally
        {
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }
}
