using System;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

public class PosSaleTests : IClassFixture<WpfAppFixture>
{
    private readonly WpfAppFixture _fixture;

    public PosSaleTests(WpfAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void PosView_WhenSearchInputPresent_CanEnterQueryAndLocateCheckoutButton()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        // First authenticate if login screen is displayed
        TestHelper.EnsureLoggedIn(window);

        // Wait for POS view to load
        var searchInput = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_SearchInput"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)
        );

        Assert.NotNull(searchInput.Result);
        searchInput.Result.Text = "Harina";

        var checkoutButton = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_CheckoutButton"))?.AsButton();
        Assert.NotNull(checkoutButton);
    }
}
