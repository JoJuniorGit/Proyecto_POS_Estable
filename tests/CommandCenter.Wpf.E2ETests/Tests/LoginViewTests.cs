using System;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

public class LoginViewTests : IClassFixture<WpfAppFixture>
{
    private readonly WpfAppFixture _fixture;

    public LoginViewTests(WpfAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void LoginView_ShouldExposeRequiredAutomationElements()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        // Find controls by AutomationId
        var usernameBox = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Username"))?.AsTextBox();
        var passwordBox = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Password"));
        var submitButton = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_SubmitButton"))?.AsButton();

        Assert.NotNull(usernameBox);
        Assert.NotNull(passwordBox);
        Assert.NotNull(submitButton);
    }

    [Fact]
    public void LoginView_WhenInvalidCredentials_ShouldDisplayErrorMessage()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        var usernameBox = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Username"))?.AsTextBox();
        var submitButton = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_SubmitButton"))?.AsButton();

        Assert.NotNull(usernameBox);
        Assert.NotNull(submitButton);

        usernameBox.Text = "invalid_user";
        submitButton.Invoke();

        // Retry waiting for error message
        var errorTextBlock = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("Login_ErrorMessage")),
            TimeSpan.FromSeconds(5)
        );

        Assert.NotNull(errorTextBlock.Result);
    }
}
