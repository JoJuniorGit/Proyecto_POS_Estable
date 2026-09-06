using System;
using System.Linq;
using System.Reflection;
using Core.Interfaces;
using Inventory.Module.Services;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ServicesContractRegressionTests
{
    [Fact]
    public void ISalesService_ContractIntegrity_AllPublicMethodsAreIntact()
    {
        var interfaceType = typeof(ISalesService);
        var implType = typeof(SalesService);

        var interfaceMethods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(interfaceMethods);

        foreach (var method in interfaceMethods)
        {
            var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var implMethod = implType.GetMethod(method.Name, BindingFlags.Public | BindingFlags.Instance, null, paramTypes, null);

            Assert.True(implMethod != null, $"Method '{method.Name}' on ISalesService is missing in SalesService implementation.");
            Assert.Equal(method.ReturnType, implMethod.ReturnType);
        }
    }

    [Fact]
    public void IInventoryService_ContractIntegrity_AllPublicMethodsAreIntact()
    {
        var interfaceType = typeof(IInventoryService);
        var implType = typeof(InventoryService);

        var interfaceMethods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(interfaceMethods);

        foreach (var method in interfaceMethods)
        {
            var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var implMethod = implType.GetMethod(method.Name, BindingFlags.Public | BindingFlags.Instance, null, paramTypes, null);

            Assert.True(implMethod != null, $"Method '{method.Name}' on IInventoryService is missing in InventoryService implementation.");
            Assert.Equal(method.ReturnType, implMethod.ReturnType);
        }
    }
}
