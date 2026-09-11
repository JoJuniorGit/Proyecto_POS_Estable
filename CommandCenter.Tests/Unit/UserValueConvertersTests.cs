using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using Desktop.Client.Converters;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class UserValueConvertersTests
{
    [Theory]
    [InlineData("JOseito", "Joseito")]
    [InlineData("pedro perez", "Pedro Perez")]
    [InlineData("MARIA GONZALEZ", "Maria Gonzalez")]
    [InlineData("ana-sofia", "Ana-Sofia")]
    [InlineData("", "")]
    public void Convert_TitleCaseConverter_ReturnsFormattedTitleCase(string input, string expected)
    {
        var converter = new TitleCaseConverter();

        var result = converter.Convert(input, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_TitleCaseConverterWithNull_ReturnsEmptyString()
    {
        var converter = new TitleCaseConverter();

        var result = converter.Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Convert_CollectionEmptyToVisibilityConverterWithEmptyList_ReturnsVisible()
    {
        var converter = new CollectionEmptyToVisibilityConverter();
        var emptyList = new List<string>();

        var result = converter.Convert(emptyList, typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void Convert_CollectionEmptyToVisibilityConverterWithPopulatedList_ReturnsCollapsed()
    {
        var converter = new CollectionEmptyToVisibilityConverter();
        var populatedList = new List<string> { "item1" };

        var result = converter.Convert(populatedList, typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Collapsed, result);
    }

    [Theory]
    [InlineData(0, Visibility.Visible)]
    [InlineData(1, Visibility.Collapsed)]
    [InlineData(5, Visibility.Collapsed)]
    public void Convert_CollectionEmptyToVisibilityConverterWithIntegerCount_ReturnsExpectedVisibility(int count, Visibility expected)
    {
        var converter = new CollectionEmptyToVisibilityConverter();

        var result = converter.Convert(count, typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_CollectionEmptyToVisibilityConverterWithNonCollection_ReturnsCollapsed()
    {
        var converter = new CollectionEmptyToVisibilityConverter();

        var result = converter.Convert("invalid", typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Collapsed, result);
    }
}
