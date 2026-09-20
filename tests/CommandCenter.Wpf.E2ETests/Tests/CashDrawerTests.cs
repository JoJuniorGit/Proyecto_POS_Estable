using System;
using CommandCenter.Wpf.E2ETests.Fixtures;
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
    public void CashDrawer_WhenNavigated_ExposesCashControls()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        TestHelper.NavigateTo(window, "Nav_BtnCashDrawer");

        var refreshBtn = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("CashDrawer_RefreshButton")),
            TimeSpan.FromSeconds(8)
        );
        Assert.NotNull(refreshBtn.Result);
    }
}
