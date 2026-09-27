using System;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

public class SalesHistoryTests : IClassFixture<WpfAppFixture>
{
    private readonly WpfAppFixture _fixture;

    public SalesHistoryTests(WpfAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void SalesHistoryView_WhenNavigated_ExposesTitleAndRefreshControls()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        TestHelper.NavigateTo(window, "Nav_BtnSalesHistory");

        var title = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SalesHistory_Title")),
            TimeSpan.FromSeconds(8)
        );
        Assert.NotNull(title.Result);

        var refreshBtn = window.FindFirstDescendant(cf => cf.ByAutomationId("SalesHistory_RefreshButton"));
        Assert.NotNull(refreshBtn);
    }
}
