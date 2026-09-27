using System;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

public class WpfIndependentFlowsTests : IClassFixture<WpfAppFixture>
{
    private readonly WpfAppFixture _fixture;

    public WpfIndependentFlowsTests(WpfAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ExchangeRateView_WhenNavigated_ExposesRateControls()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        TestHelper.NavigateTo(window, "Nav_BtnExchangeRate");

        var title = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("ExchangeRate_Title")),
            TimeSpan.FromSeconds(8)
        );
        Assert.NotNull(title.Result);

        var rateInput = window.FindFirstDescendant(cf => cf.ByAutomationId("ExchangeRate_RateInput"));
        Assert.NotNull(rateInput);

        var syncBtn = window.FindFirstDescendant(cf => cf.ByAutomationId("ExchangeRate_SyncButton"));
        Assert.NotNull(syncBtn);
    }

    [Fact]
    public void ImportProductsView_WhenNavigated_ExposesUploadControls()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        TestHelper.NavigateTo(window, "Nav_BtnImportProducts");

        var title = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("ImportProducts_Title")),
            TimeSpan.FromSeconds(8)
        );
        Assert.NotNull(title.Result);

        var selectFileBtn = window.FindFirstDescendant(cf => cf.ByAutomationId("ImportProducts_SelectFileButton"));
        Assert.NotNull(selectFileBtn);
    }

    [Fact]
    public void UsersManagementView_WhenNavigated_ExposesTitle()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        TestHelper.NavigateTo(window, "Nav_BtnUsersManagement");

        var title = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("UsersManagement_Title")),
            TimeSpan.FromSeconds(8)
        );
        Assert.NotNull(title.Result);
    }

    [Fact]
    public void SettingsView_WhenNavigated_ExposesTitle()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        TestHelper.NavigateTo(window, "Nav_BtnSettings");

        var title = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Title")),
            TimeSpan.FromSeconds(8)
        );
        Assert.NotNull(title.Result);
    }
}
