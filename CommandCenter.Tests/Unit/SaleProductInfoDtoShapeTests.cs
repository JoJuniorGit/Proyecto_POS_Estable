using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Core.DTOs;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SaleProductInfoDtoShapeTests
{
    private static readonly string[] ExpectedMembers =
    {
        "Id",
        "Name",
        "IsDeleted",
        "IsActive",
        "IsCashAdvance",
        "PriceUSD",
        "PriceBsS",
        "PriceRetailUSD",
        "PriceWholesaleUSD",
        "MinWholesaleQuantity",
        "HasWholesale",
        "IsGroupHeader",
        "IsFractional",
        "UnitOfMeasure",
        "CostPriceUSD"
    };

    [Fact]
    public void SaleProductInfoDto_IsSealed()
    {
        Assert.True(typeof(SaleProductInfoDto).IsSealed);
    }

    [Fact]
    public void SaleProductInfoDto_ExposesExactlyTheFifteenMembersSalesConsumes()
    {
        var actual = typeof(SaleProductInfoDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var expected = ExpectedMembers.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SaleProductInfoDto_EveryMemberIsInitOnly()
    {
        foreach (var property in typeof(SaleProductInfoDto).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var setter = property.SetMethod;
            Assert.NotNull(setter);
            Assert.Contains(typeof(IsExternalInit), setter!.ReturnParameter.GetRequiredCustomModifiers());
        }
    }

    [Fact]
    public void SaleProductInfoDto_CostPriceUsdIsNullableDecimal()
    {
        var property = typeof(SaleProductInfoDto).GetProperty(nameof(SaleProductInfoDto.CostPriceUSD));

        Assert.NotNull(property);
        Assert.Equal(typeof(decimal?), property!.PropertyType);
    }
}
