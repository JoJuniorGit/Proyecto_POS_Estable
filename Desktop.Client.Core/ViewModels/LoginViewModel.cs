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
    private readonly IConnectionManager? _connectionManager;

    [ObservableProperty]
    private string _cedula = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isPasswordVisible;

    [ObservableProperty]
    private string _serverStatusText = "Conectando...";

    [ObservableProperty]
    private bool _isServerConnected = true;

    [ObservableProperty]
    private bool _isScanningServer;

    [ObservableProperty]
    private string _serverAddressText = "localhost:5000";

    [RelayCommand]
    private void TogglePasswordVisibility()
    {
        IsPasswordVisible = !IsPasswordVisible;
    }

    [RelayCommand]
    private async Task OpenServerSettingsAsync()
    {
        await _dialogService.ShowServerConnectionDialogAsync();
        UpdateConnectionDisplay();
    }

    [RelayCommand]
    private async Task OpenPairingQrAsync()
    {
        await _dialogService.ShowPairingQrDialogAsync();
    }

    [RelayCommand]
    private async Task SearchServerAsync()
    {
        if (_connectionManager == null) return;
        IsScanningServer = true;
        ServerStatusText = "Buscando caja principal en la red...";
        try
        {
            var recovered = await _connectionManager.AutoRecoverAsync();
            if (!recovered)
            {
                await _dialogService.ShowServerConnectionDialogAsync();
            }
        }
        finally
        {
            IsScanningServer = false;
            UpdateConnectionDisplay();
        }
    }

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    public event Action? LoginSuccess;

    public LoginViewModel(IUserService userService, IDialogService dialogService, UserSession userSession, IConnectionManager? connectionManager = null)
    {
        _userService = userService;
        _dialogService = dialogService;
        _userSession = userSession;
        _connectionManager = connectionManager;

        if (_connectionManager != null)
        {
            _connectionManager.ConnectionStatusChanged += (s, e) =>
            {
                UpdateConnectionDisplay();
            };
            UpdateConnectionDisplay();
        }
    }

    private void UpdateConnectionDisplay()
    {
        if (_connectionManager == null) return;

        IsServerConnected = _connectionManager.Status == ConnectionStatus.Connected;
        IsScanningServer = _connectionManager.Status == ConnectionStatus.Scanning;

        var addr = _connectionManager.CurrentServerAddress;
        if (Uri.TryCreate(addr, UriKind.Absolute, out var uri))
        {
            ServerAddressText = $"{uri.Host}:{uri.Port}";
        }
        else
        {
            ServerAddressText = addr;
        }

        ServerStatusText = _connectionManager.Status switch
        {
            ConnectionStatus.Connected => $"Conectado: {ServerAddressText}",
            ConnectionStatus.Scanning => "Buscando servidor POS...",
            ConnectionStatus.Connecting => "Conectando...",
            _ => $"Sin conexión ({ServerAddressText})"
        };
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
