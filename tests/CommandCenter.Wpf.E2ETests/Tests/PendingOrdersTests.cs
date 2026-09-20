using System;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

public class PendingOrdersTests : IClassFixture<WpfAppFixture>
{
    private readonly WpfAppFixture _fixture;

    public PendingOrdersTests(WpfAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void PendingOrdersView_WhenNavigated_ExposesSearchAndRefreshControls()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        TestHelper.NavigateTo(window, "Nav_BtnPendingOrders");

        var searchInput = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PendingOrders_SearchInput")),
            TimeSpan.FromSeconds(8)
        );
        Assert.NotNull(searchInput.Result);

        var refreshBtn = window.FindFirstDescendant(cf => cf.ByAutomationId("PendingOrders_RefreshButton"));
        Assert.NotNull(refreshBtn);
    }
}
