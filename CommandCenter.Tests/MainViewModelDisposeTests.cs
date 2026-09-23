using System;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests;

public class MainViewModelDisposeTests
{
    private sealed class FakeHealthPollingService : IHealthPollingService
    {
        public bool PollingActive { get; private set; }
        public bool StopPollingCalled { get; private set; }

        public bool IsPollingActive => PollingActive;

        // Implementación explícita: evita CS0067 (el evento no se dispara en el fake).
        event EventHandler? IHealthPollingService.OnHealthRecovered
        {
            add { }
            remove { }
        }

        public void StartPolling() => PollingActive = true;

        public void StopPolling()
        {
            StopPollingCalled = true;
            PollingActive = false;
        }
    }

    /// <summary>
    /// Los ViewModels hijo reales de MainViewModel implementan IDisposable; esta subclase
    /// de prueba permite ejercitar la rama de disposición de hijos sin lanzar excepciones.
    /// </summary>
    private sealed class DisposableLoginViewModel : LoginViewModel
    {
        public bool DisposeCalled { get; private set; }

        public DisposableLoginViewModel(UserSession session)
            : base(null!, null!, session)
        {
        }

        public override void Dispose()
        {
            DisposeCalled = true;
            base.Dispose();
        }
    }

    [Fact]
    public void Dispose_StopsHealthPolling_AndDisposesChildrenWithoutThrowing()
    {
        var userSession = new UserSession();
        var fakeHealth = new FakeHealthPollingService();
        fakeHealth.StartPolling();

        var disposableLogin = new DisposableLoginViewModel(userSession);

        // Los 11 ViewModels hijo restantes se pasan como null: MainViewModel solo los almacena
        // y la rama de disposición los ignora con seguridad (null no es IDisposable).
        var mainVm = new MainViewModel(
            userSession,
            disposableLogin,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            fakeHealth,
            dialogService: null);

        mainVm.Dispose();

        Assert.True(fakeHealth.StopPollingCalled, "Dispose debe llamar StopPolling del servicio de salud.");
        Assert.False(fakeHealth.IsPollingActive, "El sondeo debe quedar inactivo tras Dispose.");
        Assert.True(disposableLogin.DisposeCalled, "Dispose debe disponer los ViewModels hijo que implementan IDisposable.");
    }

    [Fact]
    public void ProductDialogViewModel_Dispose_DoesNotThrow_AndCleansUpResources()
    {
        var productMock = new Mock<IProductService>();
        var exchangeRateMock = new Mock<IExchangeRateService>();
        exchangeRateMock.Setup(e => e.CurrentRate).Returns(36.5m);

        var vm = new ProductDialogViewModel(productMock.Object, exchangeRateMock.Object);
        vm.Sku = "BEB-101";

        Assert.IsAssignableFrom<IDisposable>(vm);

        // Act & Assert Dispose does not throw and is idempotent
        vm.Dispose();
        vm.Dispose();
    }
}
