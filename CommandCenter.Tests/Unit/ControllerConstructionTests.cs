using System;
using System.Linq;
using System.Reflection;
using Backend.API.Controllers;
using Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ControllerConstructionTests
{
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
