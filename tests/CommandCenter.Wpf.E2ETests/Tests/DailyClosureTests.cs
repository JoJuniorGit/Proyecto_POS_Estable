using System;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

public class DailyClosureTests : IClassFixture<WpfAppFixture>
{
    private readonly WpfAppFixture _fixture;

    public DailyClosureTests(WpfAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void DailyClosureView_WhenNavigated_ExposesTitleAndRefreshControls()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        TestHelper.NavigateTo(window, "Nav_BtnDailyClosure");

        var title = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("DailyClosure_Title")),
            TimeSpan.FromSeconds(8)
        );
        Assert.NotNull(title.Result);

        var refreshBtn = window.FindFirstDescendant(cf => cf.ByAutomationId("DailyClosure_RefreshButton"));
        Assert.NotNull(refreshBtn);
    }
}
