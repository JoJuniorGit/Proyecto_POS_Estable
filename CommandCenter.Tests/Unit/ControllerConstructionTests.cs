using System;
using System.Linq;
using System.Reflection;
using Backend.API.Controllers;
using Backend.API.Services;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Sales.Module.Data;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ControllerConstructionTests
{
    [Fact]
    public void AuthController_CreateFactory_ResolvesAuthServiceFromDI()
    {
        // AUD-04: AuthController depends on IAuthService; this pins the DI registration so a
        // controller activation failure cannot slip through the direct-construction unit tests.
        var services = new ServiceCollection();
        services.AddDbContext<SalesDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<IPasswordPolicyService>(_ => new Core.Services.PasswordPolicyService());
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped(_ => Mock.Of<ITokenService>());

        using var provider = services.BuildServiceProvider();

        var factory = ActivatorUtilities.CreateFactory(typeof(AuthController), Type.EmptyTypes);
        var controller = factory(provider, null);

        Assert.NotNull(controller);
    }

    [Fact]
    public void ProductsController_CreateFactory_SucceedsForControllerActivation()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Mock.Of<IInventoryService>());
        services.AddScoped(_ => Mock.Of<IProductManagementService>());
        services.AddScoped(_ => Mock.Of<ICurrentUserService>());

        using var provider = services.BuildServiceProvider();

        var factory = ActivatorUtilities.CreateFactory(typeof(ProductsController), Type.EmptyTypes);
        var controller = factory(provider, null);

        Assert.NotNull(controller);
    }

    [Theory]
    [MemberData(nameof(ControllersWithMultiplePublicConstructors))]
    public void Controller_WithMultiplePublicConstructors_DeclaresSingleActivatorConstructor(Type controllerType)
    {
        var markedConstructors = controllerType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetCustomAttribute<ActivatorUtilitiesConstructorAttribute>() != null)
            .ToList();

        Assert.Single(markedConstructors);
    }

    [Theory]
    [MemberData(nameof(ControllersThatMustNotBindDbContext))]
    public void Controller_DoesNotDeclareDbContextConstructor(Type controllerType)
    {
        // AUD-11 regression guard: presentation controllers must not bind a DbContext (that pattern caused the ProductsController activation failure).
        var parameterTypeNames = controllerType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType.Name);

        Assert.DoesNotContain(parameterTypeNames, name => name.Contains("DbContext", StringComparison.Ordinal));
    }

    public static TheoryData<Type> ControllersThatMustNotBindDbContext() => new()
    {
        typeof(CashDrawerController),
        typeof(ExchangeRateController),
        typeof(SettingsController),
        typeof(UsersController),
        typeof(AuthController),
        typeof(ProductsController),
        typeof(DailyClosureController),
        typeof(ShiftsController),
        typeof(ReceiptsController),
        typeof(ReservationsController),
    };

    public static TheoryData<Type> ControllersWithMultiplePublicConstructors()
    {
        var data = new TheoryData<Type>();

        var controllers = typeof(ProductsController).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length > 1);

        foreach (var controller in controllers)
        {
            data.Add(controller);
        }

        return data;
    }
}
