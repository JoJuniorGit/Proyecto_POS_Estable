using System;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Tools;

namespace CommandCenter.Wpf.E2ETests.Fixtures;

public static class TestHelper
{
    public static void EnsureLoggedIn(Window window)
    {
        var usernameBox = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Username"))?.AsTextBox()),
            TimeSpan.FromSeconds(3)
        );

        if (usernameBox.Result != null)
        {
            usernameBox.Result.Text = "admin";
            var submitButton = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_SubmitButton"))?.AsButton();
            submitButton?.Invoke();

            // Wait until login view is dismissed
            Retry.WhileNotNull(
                () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Username"))),
                TimeSpan.FromSeconds(8)
            );
        }
    }

    /// <summary>
    /// Login real contra el backend del harness: escribe usuario y contraseña, envía y espera a que
    /// la vista de login se desmonte. Si el login falla, surfacea el mensaje de error del cliente.
    /// </summary>
    public static void EnsureLoggedInWithCredentials(Window window, string username, string password)
    {
        var usernameBox = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Username"))?.AsTextBox()),
            TimeSpan.FromSeconds(8)
        );

        if (usernameBox.Result == null)
        {
            throw new InvalidOperationException("La vista de login no apareció: Login_Username no está presente.");
        }

        var passwordBox = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Password"))),
            TimeSpan.FromSeconds(8)
        );

        if (passwordBox.Result == null)
        {
            throw new InvalidOperationException("El campo de contraseña no apareció: Login_Password no está presente.");
        }

        usernameBox.Result.Text = username;

        // WPF PasswordBox no expone su valor (seguridad); ValuePattern.SetValue es la vía soportada
        // por el peer y, si el elemento lo rechazara, se cae a teclado sobre el elemento enfocado.
        try
        {
            passwordBox.Result.AsTextBox().Text = password;
        }
        catch (Exception)
        {
            passwordBox.Result.Focus();
            FlaUI.Core.Input.Keyboard.Type(password);
        }

        var submitButton = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_SubmitButton"))?.AsButton();
        if (submitButton == null)
        {
            throw new InvalidOperationException("El botón de login no apareció: Login_SubmitButton no está presente.");
        }

        submitButton.Invoke();

        var dismissed = Retry.WhileNotNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Username"))),
            TimeSpan.FromSeconds(20)
        );

        if (!dismissed.Success)
        {
            var errorMessage = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_ErrorMessage"))?.Name;
            throw new InvalidOperationException(
                $"El login real no completó: la vista de login sigue visible. " +
                $"Error mostrado por el cliente: '{errorMessage ?? "(sin mensaje)"}'.");
        }
    }

    public static bool NavigateTo(Window window, string navButtonAutomationId)
        => NavigateTo(window, navButtonAutomationId, TimeSpan.FromSeconds(8));

    /// <summary>
    /// Navega por la barra lateral dentro del presupuesto indicado. El contenido del DrawerHost
    /// derecho queda deshabilitado mientras el drawer está cerrado, por lo que si el botón de
    /// navegación no está accionable se abre con el menú de la vista actual y se reintenta; una
    /// transición de UIA nunca aborta la navegación.
    /// </summary>
    public static bool NavigateTo(Window window, string navButtonAutomationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        do
        {
            var navButton = FindEnabledButton(window, navButtonAutomationId);
            if (navButton != null && TryInvoke(navButton))
            {
                return true;
            }

            TryOpenSidebar(window);
            Thread.Sleep(150);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static Button? FindEnabledButton(Window window, string automationId)
    {
        try
        {
            var button = UiaRetry.RetryUia(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.AsButton());
            return button is { IsEnabled: true } ? button : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static bool TryInvoke(Button button)
    {
        try
        {
            button.Invoke();
            return true;
        }
        catch (ElementNotEnabledException)
        {
            return false;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static void TryOpenSidebar(Window window)
    {
        foreach (var menuAutomationId in new[] { "Shell_OpenMenuButton", "Pos_OpenMenuButton" })
        {
            var menuButton = FindEnabledButton(window, menuAutomationId);
            if (menuButton != null && TryInvoke(menuButton))
            {
                return;
            }
        }
    }
}
