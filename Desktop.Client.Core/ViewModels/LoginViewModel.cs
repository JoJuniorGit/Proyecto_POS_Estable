using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IUserService _userService;
    private readonly IDialogService _dialogService;
    private readonly UserSession _userSession;

    [ObservableProperty]
    private string _cedula = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isPasswordVisible;

    [RelayCommand]
    private void TogglePasswordVisibility()
    {
        IsPasswordVisible = !IsPasswordVisible;
    }

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    public event Action? LoginSuccess;

    public LoginViewModel(IUserService userService, IDialogService dialogService, UserSession userSession)
    {
        _userService = userService;
        _dialogService = dialogService;
        _userSession = userSession;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Cedula))
        {
            ErrorMessage = "Por favor ingrese su usuario.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Ingrese su contraseña.";
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var result = await _userService.LoginAsync(Cedula.Trim(), Password);
            if (result == null)
            {
                ErrorMessage = "Usuario o contraseña incorrectos.";
                return;
            }

            if (result.RequiresPasswordChange)
            {
                await HandlePasswordChangeAsync();
                return;
            }

            if (result.User != null)
            {
                _userSession.SetUser(result.User, result.Token);
                Cedula = string.Empty;
                Password = string.Empty;
                IsPasswordVisible = false;
                LoginSuccess?.Invoke();
            }
            else
            {
                ErrorMessage = "Usuario o contraseña incorrectos.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task HandlePasswordChangeAsync()
    {
        var dialogResult = await _dialogService.ShowChangePasswordDialogAsync();
        if (dialogResult == null)
        {
            ErrorMessage = "No se pudo abrir el diálogo de cambio de contraseña.";
            return;
        }

        if (!dialogResult.Value.success)
        {
            ErrorMessage = "Debe cambiar su contraseña antes de continuar.";
            return;
        }

        try
        {
            var cedula = Cedula.Trim();
            await _userService.ChangePasswordAsync(cedula, dialogResult.Value.currentPassword, dialogResult.Value.newPassword);

            // Reintentar el login con la nueva contraseña.
            var retry = await _userService.LoginAsync(cedula, dialogResult.Value.newPassword);
            if (retry?.User != null)
            {
                _userSession.SetUser(retry.User, retry.Token);
                Cedula = string.Empty;
                Password = string.Empty;
                LoginSuccess?.Invoke();
            }
            else
            {
                ErrorMessage = "Contraseña actualizada. Vuelva a iniciar sesión.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "No se pudo cambiar la contraseña: " + ex.Message;
        }
    }

    [RelayCommand]
    private void AppendDigit(string digit)
    {
        Cedula += digit;
    }

    [RelayCommand]
    private void Backspace()
    {
        if (!string.IsNullOrEmpty(Cedula))
        {
            Cedula = Cedula.Substring(0, Cedula.Length - 1);
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Cedula = string.Empty;
        Password = string.Empty;
        IsPasswordVisible = false;
        ErrorMessage = string.Empty;
    }
}
