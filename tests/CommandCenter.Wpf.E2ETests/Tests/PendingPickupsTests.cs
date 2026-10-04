using System;
using System.Linq;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

public class PendingPickupsTests : IClassFixture<WpfAppFixture>
{
    private readonly WpfAppFixture _fixture;

    public PendingPickupsTests(WpfAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void PendingPickupsView_WithCustodyRows_RendersRowsAndControls()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        Assert.True(TestHelper.NavigateTo(window, "Nav_BtnPendingPickups"));

        var searchInput = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PendingPickups_SearchInput")),
            TimeSpan.FromSeconds(8));
        var refreshButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PendingPickups_RefreshButton"))?.AsButton(),
            TimeSpan.FromSeconds(8));
        var confirmButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PendingPickups_ConfirmButton"))?.AsButton(),
            TimeSpan.FromSeconds(8));
        var deliverAllButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PendingPickups_DeliverAllButton"))?.AsButton(),
            TimeSpan.FromSeconds(8));

        Assert.NotNull(searchInput.Result);
        Assert.NotNull(refreshButton.Result);
        Assert.NotNull(confirmButton.Result);
        Assert.NotNull(deliverAllButton.Result);
        AssertAppIsRunning();
    }

    [Fact]
    public void ConfirmButton_OpensDispatchModal_AndEscapeClosesIt()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        Assert.True(TestHelper.NavigateTo(window, "Nav_BtnPendingPickups"));

        var confirmButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PendingPickups_ConfirmButton"))?.AsButton(),
            TimeSpan.FromSeconds(8));
        Assert.NotNull(confirmButton.Result);
        confirmButton.Result!.Invoke();

        var dialogConfirmButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PartialDelivery_ConfirmButton")),
            TimeSpan.FromSeconds(8));
        Assert.NotNull(dialogConfirmButton.Result);
        var dialogQuantityInput = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PartialDelivery_QuantityInput"))?.AsTextBox(),
            TimeSpan.FromSeconds(8));
        Assert.NotNull(dialogQuantityInput.Result);

        dialogQuantityInput.Result!.Focus();
        Keyboard.Press(VirtualKeyShort.ESCAPE);

        AssertPartialDeliveryDialogClosed(window);
        AssertAppIsRunning();
    }

    [Fact]
    public void CancelButton_ClosesModal()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        Assert.True(TestHelper.NavigateTo(window, "Nav_BtnPendingPickups"));

        var confirmButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PendingPickups_ConfirmButton"))?.AsButton(),
            TimeSpan.FromSeconds(8));
        Assert.NotNull(confirmButton.Result);
        confirmButton.Result!.Invoke();

        var dialogConfirmButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PartialDelivery_ConfirmButton")),
            TimeSpan.FromSeconds(8));
        Assert.NotNull(dialogConfirmButton.Result);
        var cancelButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PartialDelivery_CancelButton"))?.AsButton(),
            TimeSpan.FromSeconds(8));
        Assert.NotNull(cancelButton.Result);
        cancelButton.Result!.Invoke();

        AssertPartialDeliveryDialogClosed(window);
        AssertAppIsRunning();
    }

    [Fact]
    public void DeliverAllButton_OpensPrefilledModal_AndSingleConfirmClosesIt()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);
        Assert.True(TestHelper.NavigateTo(window, "Nav_BtnPendingPickups"));

        var deliverAllButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PendingPickups_DeliverAllButton"))?.AsButton(),
            TimeSpan.FromSeconds(8));
        Assert.NotNull(deliverAllButton.Result);
        deliverAllButton.Result!.Invoke();

        var quantityInput = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PartialDelivery_QuantityInput"))?.AsTextBox(),
            TimeSpan.FromSeconds(8));
        Assert.NotNull(quantityInput.Result);
        Assert.Equal("1", quantityInput.Result!.Text);

        var confirmButton = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PartialDelivery_ConfirmButton"))?.AsButton(),
            TimeSpan.FromSeconds(8));
        Assert.NotNull(confirmButton.Result);
        confirmButton.Result!.Invoke();

        AssertPartialDeliveryDialogClosed(window);

        var printOffer = Retry.WhileNull(FindPrintOfferWindow, TimeSpan.FromSeconds(8));
        if (printOffer.Result is not null)
        {
            printOffer.Result.Focus();
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Retry.WhileNotNull(FindPrintOfferWindow, TimeSpan.FromSeconds(8));
            Assert.Null(FindPrintOfferWindow());
        }

        AssertAppIsRunning();
    }

    private static void AssertPartialDeliveryDialogClosed(Window window)
    {
        Retry.WhileNotNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("PartialDelivery_ConfirmButton")),
            TimeSpan.FromSeconds(8));
        Assert.Null(window.FindFirstDescendant(cf => cf.ByAutomationId("PartialDelivery_ConfirmButton")));
    }

    private Window? FindPrintOfferWindow() => _fixture.App?.GetAllTopLevelWindows(_fixture.Automation)
        .FirstOrDefault(candidate => string.Equals(
            candidate.Properties.AutomationId.ValueOrDefault,
            "CustomDialogWindow_Confirm",
            StringComparison.Ordinal));

    private void AssertAppIsRunning()
    {
        Assert.NotNull(_fixture.App);
        Assert.False(_fixture.App!.HasExited);
    }
}
