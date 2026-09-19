using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class InventoryServiceReadBoundaryTests
{
    [Fact]
    public void IInventoryService_ReadMembers_DoNotReturnProductEntity()
    {
        var readReturnTypes = typeof(IInventoryService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.StartsWith("Get", StringComparison.Ordinal))
            .Select(m => m.ReturnType)
            .ToList();

        Assert.DoesNotContain(readReturnTypes, ReferencesProductEntity);
    }

    [Fact]
    public void IInventoryService_SkuLookupMember_IsRemoved()
    {
        var memberNames = typeof(IInventoryService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain("GetProductBySkuAsync", memberNames);
        Assert.DoesNotContain("GetProductByIdAsync", memberNames);
        Assert.DoesNotContain("GetProductsByIdsAsync", memberNames);
    }

    [Fact]
    public void IInventoryService_ProductReads_ReturnSaleProductInfoDto()
    {
        Assert.Equal(
            typeof(Task<IReadOnlyList<SaleProductInfoDto>>),
            typeof(IInventoryService).GetMethod(nameof(IInventoryService.GetSaleProductsByIdsAsync))!.ReturnType);
        Assert.Equal(
            typeof(Task<SaleProductInfoDto>),
            typeof(IInventoryService).GetMethod(nameof(IInventoryService.GetSaleProductByIdAsync))!.ReturnType);
        Assert.Equal(
            typeof(Task<SaleProductInfoDto>),
            typeof(IInventoryService).GetMethod(nameof(IInventoryService.GetCashAdvanceProductAsync))!.ReturnType);
    }

    private static bool ReferencesProductEntity(Type type)
    {
        if (type == typeof(Core.Entities.Product))
        {
            return true;
        }

        if (type.IsArray)
        {
            return ReferencesProductEntity(type.GetElementType()!);
        }

        if (type.IsGenericType)
        {
            return type.GetGenericArguments().Any(ReferencesProductEntity);
        }

        return false;
    }
}
