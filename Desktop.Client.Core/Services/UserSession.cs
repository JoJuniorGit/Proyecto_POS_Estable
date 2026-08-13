using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.DTOs;
using Core.Entities;

namespace Desktop.Client.Services;

public partial class UserSession : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedIn))]
    [NotifyPropertyChangedFor(nameof(IsAdmin))]
    [NotifyPropertyChangedFor(nameof(IsCashier))]
    [NotifyPropertyChangedFor(nameof(CanMutateCatalog))]
    [NotifyPropertyChangedFor(nameof(CanMutateSettings))]
    [NotifyPropertyChangedFor(nameof(CanMutateExchangeRate))]
    [NotifyPropertyChangedFor(nameof(UserName))]
    [NotifyPropertyChangedFor(nameof(UserRoleDisplay))]
    private UserDto? _currentUser;

    public bool IsLoggedIn => CurrentUser != null;
    public bool IsAdmin => CurrentUser != null && CurrentUser.Role == UserRole.Admin;
    public bool IsCashier => CurrentUser != null && CurrentUser.Role == UserRole.Cashier;

    public bool CanMutateCatalog => CurrentUser != null && CurrentUser.Role != UserRole.Cashier;
    public bool CanMutateSettings => CurrentUser != null && CurrentUser.Role != UserRole.Cashier;
    public bool CanMutateExchangeRate => CurrentUser != null && CurrentUser.Role != UserRole.Cashier;

    public string UserName => CurrentUser != null ? CurrentUser.Name : "Sin sesión";
    public string UserRoleDisplay => CurrentUser != null ? (CurrentUser.Role == UserRole.Admin ? "Administrador" : "Cajero") : "";

    public event Action? SessionChanged;

    public void SetUser(UserDto user)
    {
        CurrentUser = user;
        SessionChanged?.Invoke();
    }

    public void Logout()
    {
        CurrentUser = null;
        SessionChanged?.Invoke();
    }
}
