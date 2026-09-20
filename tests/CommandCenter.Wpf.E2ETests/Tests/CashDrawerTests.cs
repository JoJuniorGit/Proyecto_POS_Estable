using System;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

public class CashDrawerTests : IClassFixture<WpfAppFixture>
{
    private readonly WpfAppFixture _fixture;

    public CashDrawerTests(WpfAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CashDrawer_WindowOrView_CanBeLaunchedAndVerified()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        // Window title or main host verification
        Assert.False(string.IsNullOrWhiteSpace(window.Title));
    }
}
