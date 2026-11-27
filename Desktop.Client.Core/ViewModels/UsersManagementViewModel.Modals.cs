using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Estado de los modales independientes de edición y del aviso no bloqueante. El formulario
/// hidrata copias de los campos; el DTO de la grilla no se muta hasta confirmar el guardado.
/// </summary>
public partial class UsersManagementViewModel
{
    [ObservableProperty]
    private bool _isUserModalOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUserModalError))]
    private string? _userModalError;

    [ObservableProperty]
    private bool _isUserSaving;

    [ObservableProperty]
    private string? _toastMessage;

    [ObservableProperty]
    private bool _isToastVisible;

    private CancellationTokenSource? _toastCts;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    public bool HasUserModalError => !string.IsNullOrWhiteSpace(UserModalError);

    public string UserModalTitle => IsEditing ? "Editar Usuario" : "Nuevo Usuario";

    public string UserModalSubtitle => IsEditing
        ? "Actualiza las credenciales y el nivel de acceso"
        : "Configura credenciales y nivel de acceso al sistema";

    [RelayCommand]
    private void OpenEditUser(UserDto? user)
    {
        if (user is null) return;

        SelectedUser = user;
        HydrateUserForm(user);
        UserModalError = null;
        IsUserSaving = false;
        IsUserModalOpen = true;
    }

    [RelayCommand]
    private void CloseUserModal()
    {
        IsUserModalOpen = false;
        UserModalError = null;
        IsPasswordVisible = false;
        SelectedUser = null;
        ResetForm();
    }

    [RelayCommand]
    private void ClearUserSearch() => UserSearchQuery = string.Empty;

    [RelayCommand]
    private void ClearUserFilters()
    {
        UserSearchQuery = string.Empty;
        SelectedUserRoleFilter = UserRoleFilters[0];
        SelectedUserStatusFilter = UserStatusFilters[0];
        UserCurrentPage = 1;
        ApplyUserFilters();
    }

    public void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = new CancellationTokenSource();
        ToastMessage = message;
        IsToastVisible = true;
        _ = HideToastAsync(_toastCts.Token);
    }

    [RelayCommand]
    private void DismissToast()
    {
        _toastCts?.Cancel();
        IsToastVisible = false;
    }

    private async Task HideToastAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3.5), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            if (_uiContext is not null)
            {
                _uiContext.Post(_ => IsToastVisible = false, null);
            }
            else
            {
                IsToastVisible = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HydrateUserForm(UserDto user)
    {
        IsEditing = true;
        Cedula = user.Cedula;
        Name = user.Name;
        Role = user.Role;
        IsActive = user.IsActive;
        Password = string.Empty;
        IsPasswordVisible = false;
    }
}
