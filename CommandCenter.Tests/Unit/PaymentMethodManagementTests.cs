using CommunityToolkit.Mvvm.Messaging;
using Core.Constants;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PaymentMethodManagementTests
{
    private SalesDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    [Fact]
    public async Task CreateAsync_WithUniqueName_CreatesSuccessfullyAndNotifies()
    {
        using var context = CreateInMemoryDbContext();
        var mockCache = new Mock<IMemoryCache>();
        var mockNotifier = new Mock<IPaymentMethodNotifier>();
        var service = new PaymentMethodService(context, mockCache.Object, mockNotifier.Object);

        var method = new PaymentMethod
        {
            Name = "Zelle Transfer",
            IsActive = true,
            IsCash = false
        };

        var created = await service.CreateAsync(method);

        Assert.NotNull(created);
        Assert.Equal("Zelle Transfer", created.Name);
        Assert.False(created.IsDeleted);
        mockNotifier.Verify(n => n.NotifyPaymentMethodsUpdatedAsync(), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_DuplicateNameCaseInsensitive_ThrowsArgumentException()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsDeleted = false, IsActive = true });
        await context.SaveChangesAsync();

        var service = new PaymentMethodService(context);

        var duplicate = new PaymentMethod
        {
            Name = "efectivo usd",
            IsActive = true,
            IsCash = true
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(duplicate));
        Assert.Contains("Ya existe", ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_ModifiesFieldsAndNotifies()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Punto de Venta", IsDeleted = false, IsActive = true, IsCash = false });
        await context.SaveChangesAsync();

        var mockNotifier = new Mock<IPaymentMethodNotifier>();
        var service = new PaymentMethodService(context, notifier: mockNotifier.Object);

        var toUpdate = new PaymentMethod
        {
            Id = 1,
            Name = "Punto Débito/Crédito",
            IsActive = true,
            IsCash = false,
            RequiresReference = true,
            DisplayOrder = 2
        };

        var result = await service.UpdateAsync(toUpdate);

        Assert.Equal("Punto Débito/Crédito", result.Name);
        Assert.True(result.RequiresReference);
        mockNotifier.Verify(n => n.NotifyPaymentMethodsUpdatedAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_OnDeletedMethod_ThrowsInvalidOperationException()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Método Antiguo", IsDeleted = true, IsActive = false });
        await context.SaveChangesAsync();

        var service = new PaymentMethodService(context);
        var toUpdate = new PaymentMethod { Id = 1, Name = "Método Renombrado" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(toUpdate));
    }

    [Fact]
    public async Task UpdateAsync_DuplicateNameWithAnotherMethod_ThrowsArgumentException()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo", IsDeleted = false, IsActive = true },
            new PaymentMethod { Id = 2, Name = "Zelle", IsDeleted = false, IsActive = true }
        );
        await context.SaveChangesAsync();

        var service = new PaymentMethodService(context);
        var toUpdate = new PaymentMethod { Id = 2, Name = "EFECTIVO" };

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(toUpdate));
    }

    [Fact]
    public async Task DeleteAsync_WhenMethodHasSales_PerformsSoftDelete()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Tarjeta de Crédito", IsDeleted = false, IsActive = true, IsCash = false });
        context.SalePayments.Add(new SalePayment { Id = 101, SaleId = 5, PaymentMethodId = 1, Amount = 20m, AmountBsS = 1000m });
        await context.SaveChangesAsync();

        var mockNotifier = new Mock<IPaymentMethodNotifier>();
        var service = new PaymentMethodService(context, notifier: mockNotifier.Object);

        await service.DeleteAsync(1);

        var inDb = await context.PaymentMethods.FindAsync(1);
        Assert.NotNull(inDb);
        Assert.True(inDb.IsDeleted);
        Assert.False(inDb.IsActive);
        mockNotifier.Verify(n => n.NotifyPaymentMethodsUpdatedAsync(), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenMethodHasClosures_PerformsSoftDelete()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 2, Name = "Pago Móvil", IsDeleted = false, IsActive = true, IsCash = false });
        context.ClosureDetails.Add(new ClosureDetail { Id = 201, DailyClosureId = 1, PaymentMethodId = 2, ExpectedAmountBsS = 50m, ActualAmountBsS = 50m });
        await context.SaveChangesAsync();

        var service = new PaymentMethodService(context);

        await service.DeleteAsync(2);

        var inDb = await context.PaymentMethods.FindAsync(2);
        Assert.NotNull(inDb);
        Assert.True(inDb.IsDeleted);
        Assert.False(inDb.IsActive);
    }

    [Fact]
    public async Task DeleteAsync_WhenPhysicalCashAndHasPhysicalCashTransactions_PerformsSoftDelete_Per_MT001()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 3, Name = "Efectivo Bolívares", IsDeleted = false, IsActive = true, IsCash = true });
        // Simular transacción física de gaveta (IsPhysicalCash = true, regla MT-001)
        context.CashTransactions.Add(new CashTransaction { Id = 301, SessionId = 1, AmountUsd = 10m, IsPhysicalCash = true, Description = "Ingreso inicial gaveta" });
        await context.SaveChangesAsync();

        var service = new PaymentMethodService(context);

        await service.DeleteAsync(3);

        var inDb = await context.PaymentMethods.FindAsync(3);
        Assert.NotNull(inDb);
        Assert.True(inDb.IsDeleted);
        Assert.False(inDb.IsActive);
    }

    [Fact]
    public async Task DeleteAsync_WhenUnusedDigitalMethod_PerformsHardDelete()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 4, Name = "Cripto USDT", IsDeleted = false, IsActive = true, IsCash = false });
        await context.SaveChangesAsync();

        var mockNotifier = new Mock<IPaymentMethodNotifier>();
        var service = new PaymentMethodService(context, notifier: mockNotifier.Object);

        await service.DeleteAsync(4);

        var inDb = await context.PaymentMethods.FindAsync(4);
        Assert.Null(inDb);
        mockNotifier.Verify(n => n.NotifyPaymentMethodsUpdatedAsync(), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_AlreadyDeletedMethod_ThrowsInvalidOperationException()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 5, Name = "Eliminado", IsDeleted = true, IsActive = false });
        await context.SaveChangesAsync();

        var service = new PaymentMethodService(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(5));
    }

    [Fact]
    public async Task GetAllAsync_ExcludesDeletedMethods()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Activo 1", IsDeleted = false, IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 2, Name = "Inactivo", IsDeleted = false, IsActive = false, DisplayOrder = 2 },
            new PaymentMethod { Id = 3, Name = "Borrado", IsDeleted = true, IsActive = false, DisplayOrder = 3 }
        );
        await context.SaveChangesAsync();

        var service = new PaymentMethodService(context);
        var list = (await service.GetAllAsync()).ToList();

        Assert.Equal(2, list.Count);
        Assert.DoesNotContain(list, m => m.Id == 3);
    }

    [Fact]
    public async Task GetActiveMethodsAsync_ExcludesDeletedAndInactiveMethods()
    {
        using var context = CreateInMemoryDbContext();
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Activo 1", IsDeleted = false, IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 2, Name = "Inactivo", IsDeleted = false, IsActive = false, DisplayOrder = 2 },
            new PaymentMethod { Id = 3, Name = "Borrado", IsDeleted = true, IsActive = false, DisplayOrder = 3 }
        );
        await context.SaveChangesAsync();

        var service = new PaymentMethodService(context);
        var activeList = (await service.GetActiveMethodsAsync()).ToList();

        Assert.Single(activeList);
        Assert.Equal(1, activeList[0].Id);
    }

    [Fact]
    public async Task SettingsViewModel_AddNewMethodAsync_DefaultsToDigital()
    {
        var mockPayment = new Mock<IPaymentService>();
        var mockSettings = new Mock<ISettingsService>();
        var mockDialog = new Mock<IDialogService>();

        mockPayment.Setup(p => p.GetAllMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto>());

        mockDialog.Setup(d => d.ShowTextInputAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("Nuevo Digital");

        PaymentMethodDto? createdDto = null;
        mockPayment.Setup(p => p.CreateAsync(It.IsAny<PaymentMethodDto>()))
            .Callback<PaymentMethodDto>(dto => createdDto = dto)
            .ReturnsAsync((PaymentMethodDto dto) => dto);

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Admin", Role = Core.Entities.UserRole.Admin });

        using var vm = new SettingsViewModel(
            mockPayment.Object,
            mockSettings.Object,
            session,
            mockDialog.Object);

        await vm.AddNewMethodCommand.ExecuteAsync(null);

        Assert.NotNull(createdDto);
        Assert.Equal("Nuevo Digital", createdDto.Name);
        Assert.False(createdDto.IsCash); // Regla obligatoria: Digital por defecto
    }

    [Fact]
    public async Task SettingsViewModel_TogglePaymentTypeAsync_InvertsIsCashAndCallsService()
    {
        var mockPayment = new Mock<IPaymentService>();
        var mockSettings = new Mock<ISettingsService>();

        var dto = new PaymentMethodDto { Id = 10, Name = "Efectivo", IsCash = true, IsActive = true };
        mockPayment.Setup(p => p.GetAllMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto> { dto });

        mockPayment.Setup(p => p.UpdateAsync(It.IsAny<PaymentMethodDto>()))
            .ReturnsAsync((PaymentMethodDto input) => input);

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Admin", Role = Core.Entities.UserRole.Admin });

        using var vm = new SettingsViewModel(
            mockPayment.Object,
            mockSettings.Object,
            session);

        await vm.EnsureLoadedAsync();

        var target = vm.PaymentMethods.First();
        Assert.True(target.IsCash);

        await vm.TogglePaymentTypeCommand.ExecuteAsync(target);

        mockPayment.Verify(p => p.UpdateAsync(It.Is<PaymentMethodDto>(m => m.Id == 10 && !m.IsCash)), Times.Once);
        Assert.False(vm.PaymentMethods.First().IsCash);
    }
}
