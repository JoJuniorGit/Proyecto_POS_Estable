using System;
using Core.Helpers;
using Xunit;

namespace CommandCenter.Tests.Helpers;

public class PricingCalculatorTests
{
    [Theory]
    [InlineData(10.455, 10.46)]
    [InlineData(10.454, 10.45)]
    [InlineData(10.445, 10.45)]
    [InlineData(0.00, 0.00)]
    [InlineData(-10.455, -10.46)]
    public void RoundToDigital_FollowsAwayFromZeroRule(decimal input, decimal expected)
    {
        var result = PricingCalculator.RoundToDigital(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(100.50, 101)]
    [InlineData(100.49, 100)]
    [InlineData(0.50, 1)]
    [InlineData(0.49, 0)]
    public void RoundToCash_ReturnsInteger(decimal input, decimal expected)
    {
        var result = PricingCalculator.RoundToCash(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1.00, 36.50, 36.50)]
    [InlineData(1.25, 36.42, 45.53)]
    [InlineData(0.00, 36.50, 0.00)]
    [InlineData(10.00, 0.00, 0.00)]
    public void ToBsS_ConvertsAndRoundsCorrectly(decimal usd, decimal rate, decimal expected)
    {
        var result = PricingCalculator.ToBsS(usd, rate);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(36.50, 36.50, 1.00)]
    [InlineData(45.53, 36.42, 1.25)]
    [InlineData(0.00, 36.50, 0.00)]
    [InlineData(10.00, 0.00, 0.00)]
    public void ToUSD_ConvertsAndRoundsCorrectly(decimal bss, decimal rate, decimal expected)
    {
        var result = PricingCalculator.ToUSD(bss, rate);
        Assert.Equal(expected, result);
    }
}
