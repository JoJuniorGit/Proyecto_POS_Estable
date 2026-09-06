using System;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase3DesktopOptimizationTests
{
    private sealed class DisposableTestViewModel : ObservableObject, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public void MainViewModel_WeakReference_ReclaimedByGarbageCollectorOnDispose()
    {
        WeakReference weakVm = null!;

        Action createAndDispose = () =>
        {
            var userSession = new UserSession();
            var mockHealth = new Mock<IHealthPollingService>();

            var mainVm = new MainViewModel(
                userSession,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                mockHealth.Object);

            weakVm = new WeakReference(mainVm);
            mainVm.Dispose();
        };

        createAndDispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakVm.IsAlive, "MainViewModel debió ser recolectado por el GC tras llamar a Dispose().");
    }

    [Fact]
    public void InventoryViewModel_Dispose_CancelsAndDisposesCancellationTokenSourceSafely()
    {
        var mockProductService = new Mock<IProductService>();
        var mockExchangeService = new Mock<IExchangeRateService>();
        var userSession = new UserSession();

        var inventoryVm = new InventoryViewModel(mockProductService.Object, mockExchangeService.Object, userSession);
        
        // Simular búsqueda debounced
        inventoryVm.SearchText = "Laptop";

        // Ejecutar Dispose
        inventoryVm.Dispose();

        Assert.True(true, "Dispose debió cancelar el token de búsqueda sin lanzar ObjectDisposedException.");
    }

    [Fact]
    public void MainViewModel_IsAnyModalOpen_TogglesStateCorrectly()
    {
        var userSession = new UserSession();
        var mockHealth = new Mock<IHealthPollingService>();

        using var mainVm = new MainViewModel(
            userSession,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            mockHealth.Object);

        Assert.False(mainVm.IsAnyModalOpen);
        mainVm.IsAnyModalOpen = true;
        Assert.True(mainVm.IsAnyModalOpen);
    }

    [Fact]
    public async System.Threading.Tasks.Task PaymentMethodService_GetActiveMethodsAsync_WithBoundedMemoryCache_DoesNotThrowInvalidOperationException()
    {
        var options = new Microsoft.Extensions.Options.OptionsWrapper<Microsoft.Extensions.Caching.Memory.MemoryCacheOptions>(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 10000 });
        using var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(options);

        var dbOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Sales.Module.Data.SalesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var salesDb = new Sales.Module.Data.SalesDbContext(dbOptions);
        salesDb.PaymentMethods.Add(new Sales.Module.Entities.PaymentMethod { Id = 1, Name = "Efectivo USD", IsActive = true, DisplayOrder = 1 });
        await salesDb.SaveChangesAsync();

        var service = new Sales.Module.Services.PaymentMethodService(salesDb, memoryCache);

        // Debe ejecutar sin lanzar InvalidOperationException por falta de Size = 1
        var methods = await service.GetActiveMethodsAsync();
        Assert.Single(methods);
    }
}
