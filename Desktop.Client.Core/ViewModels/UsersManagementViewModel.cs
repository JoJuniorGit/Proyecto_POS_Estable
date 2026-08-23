using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class UsersManagementViewModel : ObservableObject
{
    private readonly IUserService _userService;
    private readonly UserSession _userSession;

    public CustomerManagementViewModel CustomerViewModel { get; }

    [ObservableProperty]
    private int _selectedTabIndex = 0; // 0: Users, 1: Customers

    [ObservableProperty]
    private ObservableCollection<UserDto> _users = new();

    [ObservableProperty]
    private UserDto? _selectedUser;

    [ObservableProperty]
    private string _cedula = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private UserRole _role = UserRole.Cashier;

    [ObservableProperty]
    private bool _isActive = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isEditing;

    public string PasswordHint => IsEditing 
        ? "Contraseña (Dejar en blanco para conservar actual)" 
        : "Contraseña (Opcional, por defecto Cédula)";

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(PasswordHint));
    }

    public Array Roles => Enum.GetValues(typeof(UserRole));

    public UsersManagementViewModel(
        IUserService userService,
        UserSession userSession,
        CustomerManagementViewModel customerViewModel)
    {
        _userService = userService;
        _userSession = userSession;
        CustomerViewModel = customerViewModel;
    }

    public async Task EnsureLoadedAsync()
    {
        await LoadUsersAsync();
        await CustomerViewModel.InitializeAsync();
    }

    [RelayCommand]
    private async Task LoadUsersAsync()
    {
        try
        {
            StatusMessage = "Cargando usuarios...";
            var list = await _userService.GetUsersAsync();
            Users = new ObservableCollection<UserDto>(list);
            StatusMessage = $"Total de usuarios: {Users.Count}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    partial void OnSelectedUserChanged(UserDto? value)
    {
        if (value != null)
        {
            IsEditing = true;
            Cedula = value.Cedula;
            Name = value.Name;
            Role = value.Role;
            IsActive = value.IsActive;
            Password = string.Empty;
        }
        else
        {
            ResetForm();
        }
    }

    [RelayCommand]
    private void NewUser()
    {
        SelectedUser = null;
        ResetForm();
    }

    private void ResetForm()
    {
        IsEditing = false;
        Cedula = string.Empty;
        Name = string.Empty;
        Password = string.Empty;
        Role = UserRole.Cashier;
        IsActive = true;
    }

    [RelayCommand]
    private async Task SaveUserAsync()
    {
        if (string.IsNullOrWhiteSpace(Cedula) || string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "Cédula y Nombre son obligatorios.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(Password) && Password.Trim().Length < 4)
        {
            StatusMessage = "La contraseña personalizada debe tener al menos 4 caracteres.";
            return;
        }

        try
        {
            if (IsEditing && SelectedUser != null)
            {
                bool isMainAdmin = SelectedUser.Cedula == "V-00000000" || SelectedUser.Name == "Admin";
                if (isMainAdmin && !IsActive)
                {
                    StatusMessage = "El Administrador principal del sistema no se puede desactivar.";
                    System.Windows.MessageBox.Show(StatusMessage, "Operación No Permitida", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                if (_userSession.CurrentUser != null && SelectedUser.Id == _userSession.CurrentUser.Id && !IsActive)
                {
                    StatusMessage = "No puedes desactivar tu propia cuenta actualmente en sesión.";
                    System.Windows.MessageBox.Show(StatusMessage, "Operación No Permitida", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                var updateDto = new UpdateUserDto
                {
                    Cedula = Cedula.Trim(),
                    Name = Name.Trim(),
                    Password = string.IsNullOrWhiteSpace(Password) ? null : Password.Trim(),
                    Role = Role,
                    IsActive = isMainAdmin ? true : IsActive
                };
                var updated = await _userService.UpdateUserAsync(SelectedUser.Id, updateDto);
                StatusMessage = "Usuario actualizado correctamente.";
            }
            else
            {
                var createDto = new CreateUserDto
                {
                    Cedula = Cedula.Trim(),
                    Name = Name.Trim(),
                    Password = string.IsNullOrWhiteSpace(Password) ? null : Password.Trim(),
                    Role = Role
                };
                var created = await _userService.CreateUserAsync(createDto);
                StatusMessage = "Usuario creado correctamente.";
            }

            ResetForm();
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleActiveUserAsync(UserDto? user)
    {
        var target = user ?? SelectedUser;
        if (target == null) return;

        try
        {
            if (target.IsActive)
            {
                if (target.Cedula == "V-00000000" || target.Name == "Admin")
                {
                    StatusMessage = "El Administrador principal del sistema no se puede desactivar.";
                    System.Windows.MessageBox.Show(StatusMessage, "Operación No Permitida", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                if (_userSession.CurrentUser != null && target.Id == _userSession.CurrentUser.Id)
                {
                    StatusMessage = "No puedes desactivar tu propia cuenta actualmente en sesión.";
                    System.Windows.MessageBox.Show(StatusMessage, "Operación No Permitida", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                await _userService.SoftDeleteUserAsync(target.Id);
                StatusMessage = $"Usuario '{target.Name}' desactivado correctamente.";
            }
            else
            {
                await _userService.ReactivateUserAsync(target.Id);
                StatusMessage = $"Usuario '{target.Name}' reactivado correctamente.";
            }

            ResetForm();
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task HardDeleteUserAsync(UserDto? user)
    {
        var target = user ?? SelectedUser;
        if (target == null) return;

        if (target.Cedula == "V-00000000" || target.Name == "Admin")
        {
            StatusMessage = "El Administrador principal del sistema no se puede eliminar.";
            System.Windows.MessageBox.Show(StatusMessage, "Operación No Permitida", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (_userSession.CurrentUser != null && target.Id == _userSession.CurrentUser.Id)
        {
            StatusMessage = "No puedes eliminar tu propia cuenta actualmente en sesión.";
            System.Windows.MessageBox.Show(StatusMessage, "Operación No Permitida", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"¿Está seguro de que desea eliminar PERMANENTEMENTE al usuario '{target.Name}' ({target.Cedula})?\n\nEsta acción eliminará el usuario de la base de datos de forma definitiva.",
            "Confirmar Eliminación Definitiva",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await _userService.PermanentDeleteUserAsync(target.Id);
            StatusMessage = $"Usuario '{target.Name}' eliminado permanentemente.";
            ResetForm();
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }
}
