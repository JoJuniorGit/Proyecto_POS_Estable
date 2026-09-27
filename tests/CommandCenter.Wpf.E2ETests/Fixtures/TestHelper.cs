using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

namespace CommandCenter.Wpf.E2ETests.Fixtures;

public static class TestHelper
{
    public static void EnsureLoggedIn(Window window)
    {
        var usernameBox = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Username"))?.AsTextBox(),
            TimeSpan.FromSeconds(3)
        );

        if (usernameBox.Result != null)
        {
            usernameBox.Result.Text = "admin";
            var submitButton = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_SubmitButton"))?.AsButton();
            submitButton?.Invoke();

            // Wait until login view is dismissed
            Retry.WhileNotNull(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Username")),
                TimeSpan.FromSeconds(8)
            );
        }
    }

    public static bool NavigateTo(Window window, string navButtonAutomationId)
    {
        var navBtn = Retry.WhileNull(
            () =>
            {
                var btn = window.FindFirstDescendant(cf => cf.ByAutomationId(navButtonAutomationId))?.AsButton();
                return (btn != null && btn.IsEnabled) ? btn : null;
            },
            TimeSpan.FromSeconds(8)
        );

        if (navBtn.Result != null)
        {
            navBtn.Result.Invoke();
            return true;
        }

        return false;
    }
}
