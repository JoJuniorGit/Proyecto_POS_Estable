using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IUserService _userService;
    private readonly UserSession _userSession;

    [ObservableProperty]
    private string _cedula = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    public event Action? LoginSuccess;

    public LoginViewModel(IUserService userService, UserSession userSession)
    {
        _userService = userService;
        _userSession = userSession;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Cedula))
        {
            ErrorMessage = "Ingrese un número de Cédula válido.";
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var user = await _userService.LoginAsync(Cedula.Trim());
            if (user != null)
            {
                _userSession.SetUser(user);
                Cedula = string.Empty;
                LoginSuccess?.Invoke();
            }
            else
            {
                ErrorMessage = "Cédula no encontrada o usuario inactivo.";
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
        ErrorMessage = string.Empty;
    }
}
