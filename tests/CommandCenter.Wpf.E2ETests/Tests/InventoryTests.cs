using System;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

public class InventoryTests : IClassFixture<WpfAppFixture>
{
    private readonly WpfAppFixture _fixture;

    public InventoryTests(WpfAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void InventoryView_WhenNavigated_ExposesSearchAndRefreshControls()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        TestHelper.NavigateTo(window, "Nav_BtnInventory");

        var searchInput = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("Inventory_SearchInput")),
            TimeSpan.FromSeconds(8)
        );
        Assert.NotNull(searchInput.Result);

        var refreshBtn = window.FindFirstDescendant(cf => cf.ByAutomationId("Inventory_RefreshButton"));
        Assert.NotNull(refreshBtn);
    }
}
