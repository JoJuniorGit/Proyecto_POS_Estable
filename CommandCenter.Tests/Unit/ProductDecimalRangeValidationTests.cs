using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using Desktop.Client.ViewModels;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ProductDecimalRangeValidationTests
{
    private static readonly (Type Type, string Property)[] DecimalMoneyProperties =
    [
        (typeof(ProductDialogViewModel), "Price"),
        (typeof(ProductDialogViewModel), "Cost"),
        (typeof(ProductDialogViewModel), "ProfitPercentage"),
        (typeof(ProductDialogViewModel), "CostPriceUSD"),
        (typeof(ProductDialogViewModel), "ProfitMarginRetail"),
        (typeof(ProductDialogViewModel), "PriceRetailUSD"),
        (typeof(ProductDialogViewModel), "ProfitMarginWholesale"),
        (typeof(ProductDialogViewModel), "PriceWholesaleUSD"),
        (typeof(AddProductViewModel), "Price"),
        (typeof(AddProductViewModel), "Cost"),
        (typeof(AddProductViewModel), "StockQuantity"),
        (typeof(AddProductViewModel), "LowStockThreshold"),
    ];

    [Fact]
    public void DecimalMoneyProperties_UseDecimalOperandRange_NotDouble()
    {
        foreach (var (type, propertyName) in DecimalMoneyProperties)
        {
            var range = GetRange(type, propertyName);

            Assert.Equal(typeof(decimal), range.OperandType);
        }
    }

    [Fact]
    public void DecimalMoneyProperties_AcceptDecimalMaxValue_AndRejectNegative()
    {
        foreach (var (type, propertyName) in DecimalMoneyProperties)
        {
            var range = GetRange(type, propertyName);

            Assert.True(range.IsValid(decimal.MaxValue), $"{type.Name}.{propertyName} must accept decimal.MaxValue");
            Assert.False(range.IsValid(-1m), $"{type.Name}.{propertyName} must reject negative values");
        }
    }

    [Fact]
    public void DecimalMoneyProperties_ValidateThroughValidator_WithoutOverflow()
    {
        foreach (var (type, propertyName) in DecimalMoneyProperties)
        {
            var range = GetRange(type, propertyName);
            var results = new List<ValidationResult>();

            var valid = Validator.TryValidateValue(
                decimal.MaxValue,
                new ValidationContext(decimal.MaxValue),
                results,
                new[] { range });

            Assert.True(valid, $"{type.Name}.{propertyName}: {string.Join("; ", results.Select(r => r.ErrorMessage))}");
        }
    }

    private static RangeAttribute GetRange(Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        var range = property!.GetCustomAttribute<RangeAttribute>();

        Assert.NotNull(range);
        return range!;
    }
}
