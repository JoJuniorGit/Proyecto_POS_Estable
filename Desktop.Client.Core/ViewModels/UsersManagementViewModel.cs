using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class UsersManagementViewModel : ObservableObject, IDisposable
{
    private readonly IUserService _userService;
    private readonly UserSession _userSession;
    private readonly IDialogService? _dialogService;
    private readonly List<UserDto> _allUsers = new();
    private bool _disposed;

    public CustomerManagementViewModel CustomerViewModel { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (CustomerViewModel as IDisposable)?.Dispose();
    }

    [ObservableProperty]
    private int _selectedTabIndex = 0; // 0: Users, 1: Customers

    [ObservableProperty]
    private ObservableCollection<UserDto> _users = new();

    [ObservableProperty]
    private UserDto? _selectedUser;

    [ObservableProperty]
    private string _userSearchQuery = string.Empty;

    [ObservableProperty]
    private UserRoleFilterOption? _selectedUserRoleFilter;

    public IReadOnlyList<UserRoleFilterOption> UserRoleFilters { get; } = new[]
    {
        new UserRoleFilterOption("Todos los roles", null),
        new UserRoleFilterOption("Administrador", UserRole.Admin),
        new UserRoleFilterOption("Gerente", UserRole.Manager),
        new UserRoleFilterOption("Cajero", UserRole.Cashier)
    };

    public IReadOnlyList<UserRoleOption> RoleOptions { get; } = new[]
    {
        new UserRoleOption(UserRole.Admin, "Administrador"),
        new UserRoleOption(UserRole.Manager, "Gerente"),
        new UserRoleOption(UserRole.Cashier, "Cajero"),
        new UserRoleOption(UserRole.Driver, "Conductor")
    };

    public int TotalUserCount => _allUsers.Count;

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

    [ObservableProperty]
    private bool _isPasswordVisible;

    [RelayCommand]
    private void TogglePasswordVisibility()
    {
        IsPasswordVisible = !IsPasswordVisible;
    }

    public string PasswordHint => IsEditing 
        ? "Contraseña (Dejar en blanco para conservar actual)" 
        : "Contraseña (Opcional, por defecto Usuario)";

    public string PasswordPlaceholder => IsEditing
        ? "Dejar en blanco para conservar la actual"
        : "Opcional (por defecto: usuario)";

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(PasswordHint));
        OnPropertyChanged(nameof(PasswordPlaceholder));
    }

    partial void OnUserSearchQueryChanged(string value)
    {
        UserCurrentPage = 1;
        ApplyUserFilters();
    }

    partial void OnSelectedUserRoleFilterChanged(UserRoleFilterOption? value)
    {
        UserCurrentPage = 1;
        ApplyUserFilters();
    }

    public Array Roles => Enum.GetValues(typeof(UserRole));

    public UsersManagementViewModel(
        IUserService userService,
        UserSession userSession,
        CustomerManagementViewModel customerViewModel,
        IDialogService? dialogService = null)
    {
        _userService = userService;
        _userSession = userSession;
        CustomerViewModel = customerViewModel;
        _dialogService = dialogService;
        SelectedUserRoleFilter = UserRoleFilters[0];
    }

    public async Task EnsureLoadedAsync()
    {
        // Usuarios y clientes son lecturas independientes de la misma pantalla.
        await Task.WhenAll(LoadUsersAsync(), CustomerViewModel.InitializeAsync());
    }

    [RelayCommand]
    private async Task LoadUsersAsync()
    {
        try
        {
            StatusMessage = "Cargando usuarios...";
            var list = await _userService.GetUsersAsync();

            _allUsers.Clear();
            _allUsers.AddRange(list);
            OnPropertyChanged(nameof(TotalUserCount));
            ApplyUserFilters();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private void ApplyUserFilters()
    {
        var query = UserSearchQuery?.Trim();
        IEnumerable<UserDto> filtered = _allUsers;

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(user =>
                user.Cedula.Contains(query, StringComparison.OrdinalIgnoreCase)
                || user.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedUserRoleFilter?.Role is UserRole role)
        {
            filtered = filtered.Where(user => user.Role == role);
        }

        _filteredUsers.Clear();
        _filteredUsers.AddRange(filtered);
        UserFilteredCount = _filteredUsers.Count;
        UserTotalPages = _filteredUsers.Count == 0
            ? 0
            : (int)Math.Ceiling((double)_filteredUsers.Count / UserPageSize);

        Users.Clear();
        if (UserTotalPages == 0)
        {
            UserCurrentPage = 0;
            UpdateUserPageNumbers();
            StatusMessage = "No hay usuarios que coincidan con el filtro.";
            return;
        }

        if (UserCurrentPage <= 0) UserCurrentPage = 1;
        if (UserCurrentPage > UserTotalPages) UserCurrentPage = UserTotalPages;

        foreach (var user in _filteredUsers
                     .Skip((UserCurrentPage - 1) * UserPageSize)
                     .Take(UserPageSize))
        {
            Users.Add(user);
        }

        UpdateUserPageNumbers();
        StatusMessage = $"Mostrando {Users.Count} de {_filteredUsers.Count} usuarios";
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
            IsPasswordVisible = false;
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
        IsPasswordVisible = false;
        Role = UserRole.Cashier;
        IsActive = true;
    }

    [RelayCommand]
    private async Task SaveUserAsync()
    {
        if (string.IsNullOrWhiteSpace(Cedula) || string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "Usuario y Nombre son obligatorios.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(Password) && Password.Trim().Length < 8)
        {
            StatusMessage = "La contraseña personalizada debe tener al menos 8 caracteres.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(Password) && !Password.Any(char.IsLetter))
        {
            StatusMessage = "La contraseña personalizada debe incluir al menos una letra.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(Password) && !Password.Any(char.IsDigit))
        {
            StatusMessage = "La contraseña personalizada debe incluir al menos un número.";
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
                    if (_dialogService != null) _dialogService.ShowWarning("Operación No Permitida", StatusMessage);
                    return;
                }

                if (_userSession.CurrentUser != null && SelectedUser.Id == _userSession.CurrentUser.Id && !IsActive)
                {
                    StatusMessage = "No puedes desactivar tu propia cuenta actualmente en sesión.";
                    if (_dialogService != null) _dialogService.ShowWarning("Operación No Permitida", StatusMessage);
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
                    if (_dialogService != null) _dialogService.ShowWarning("Operación No Permitida", StatusMessage);
                    return;
                }

                if (_userSession.CurrentUser != null && target.Id == _userSession.CurrentUser.Id)
                {
                    StatusMessage = "No puedes desactivar tu propia cuenta actualmente en sesión.";
                    if (_dialogService != null) _dialogService.ShowWarning("Operación No Permitida", StatusMessage);
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
            if (_dialogService != null) _dialogService.ShowWarning("Operación No Permitida", StatusMessage);
            return;
        }

        if (_userSession.CurrentUser != null && target.Id == _userSession.CurrentUser.Id)
        {
            StatusMessage = "No puedes eliminar tu propia cuenta actualmente en sesión.";
            if (_dialogService != null) _dialogService.ShowWarning("Operación No Permitida", StatusMessage);
            return;
        }

        bool confirmed = _dialogService != null
            ? _dialogService.ShowConfirm(
                "Confirmar Eliminación Definitiva",
                $"¿Está seguro de que desea eliminar PERMANENTEMENTE al usuario '{target.Name}' (Usuario: {target.Cedula})?\n\nEsta acción eliminará el usuario de la base de datos de forma definitiva.")
            : false;

        if (!confirmed) return;

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
