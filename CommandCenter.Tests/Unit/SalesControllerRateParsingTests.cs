using Backend.API.Controllers;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SalesControllerRateParsingTests
{
    [Theory]
    [InlineData("842.21", 842.21)]
    [InlineData("84221", 84221)]
    [InlineData("0", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ParseRateInvariant_WhenDotDecimal_ParsesAsInvariantNotThousandSeparated(string? raw, decimal expected)
    {
        Assert.Equal(expected, SalesController.ParseRateInvariant(raw));
    }
}