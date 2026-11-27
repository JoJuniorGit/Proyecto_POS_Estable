using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Paginación avanzada de la pestaña de usuarios. El catálogo de usuarios se carga completo
/// (no existe endpoint paginado); la página se calcula en cliente sobre el filtro activo con
/// el mismo lenguaje visual y de navegación que el catálogo de productos.
/// </summary>
public partial class UsersManagementViewModel
{
    private const int UserPageSize = 25;
    private readonly List<UserDto> _filteredUsers = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserPageSummary))]
    [NotifyPropertyChangedFor(nameof(CanGoFirstUser))]
    [NotifyPropertyChangedFor(nameof(CanGoPreviousUser))]
    [NotifyPropertyChangedFor(nameof(CanGoNextUser))]
    [NotifyPropertyChangedFor(nameof(CanGoLastUser))]
    private int _userCurrentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserPageSummary))]
    [NotifyPropertyChangedFor(nameof(CanGoFirstUser))]
    [NotifyPropertyChangedFor(nameof(CanGoPreviousUser))]
    [NotifyPropertyChangedFor(nameof(CanGoNextUser))]
    [NotifyPropertyChangedFor(nameof(CanGoLastUser))]
    private int _userTotalPages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserPageSummary))]
    private int _userFilteredCount;

    [ObservableProperty]
    private string _userTargetPageInput = "1";

    public ObservableCollection<PageNumberItem> UserPageNumbers { get; } = new();

    public string UserPageSummary => UserFilteredCount == 0
        ? "Página 0 de 0 (0 usuarios)"
        : $"Página {UserCurrentPage} de {UserTotalPages} ({UserFilteredCount} usuarios)";

    public bool CanGoFirstUser => UserCurrentPage > 1 && UserTotalPages > 1;
    public bool CanGoPreviousUser => UserCurrentPage > 1 && UserTotalPages > 1;
    public bool CanGoNextUser => UserCurrentPage < UserTotalPages && UserTotalPages > 1;
    public bool CanGoLastUser => UserCurrentPage < UserTotalPages && UserTotalPages > 1;

    [RelayCommand]
    private void FirstUserPage()
    {
        if (!CanGoFirstUser) return;
        UserCurrentPage = 1;
        ApplyUserFilters();
    }

    [RelayCommand]
    private void PreviousUserPage()
    {
        if (!CanGoPreviousUser) return;
        UserCurrentPage--;
        ApplyUserFilters();
    }

    [RelayCommand]
    private void NextUserPage()
    {
        if (!CanGoNextUser) return;
        UserCurrentPage++;
        ApplyUserFilters();
    }

    [RelayCommand]
    private void LastUserPage()
    {
        if (!CanGoLastUser) return;
        UserCurrentPage = UserTotalPages;
        ApplyUserFilters();
    }

    [RelayCommand]
    private void GoToUserPage(int page)
    {
        if (page >= 1 && page <= UserTotalPages && page != UserCurrentPage)
        {
            UserCurrentPage = page;
            ApplyUserFilters();
        }
    }

    [RelayCommand]
    private void SubmitUserGoToPage()
    {
        if (int.TryParse(UserTargetPageInput, out int target) && UserTotalPages > 0)
        {
            int clamped = Math.Clamp(target, 1, UserTotalPages);
            if (clamped != UserCurrentPage)
            {
                UserCurrentPage = clamped;
                ApplyUserFilters();
            }
            else
            {
                UpdateUserPageNumbers();
            }
        }
        else
        {
            UserTargetPageInput = UserCurrentPage > 0 ? UserCurrentPage.ToString() : "1";
        }
    }

    public void UpdateUserPageNumbers()
    {
        UserPageNumbers.Clear();

        if (UserTotalPages <= 0)
        {
            UserTargetPageInput = "0";
            return;
        }

        if (UserCurrentPage <= 0) UserCurrentPage = 1;
        if (UserCurrentPage > UserTotalPages) UserCurrentPage = UserTotalPages;

        int startPage = Math.Max(1, UserCurrentPage - 2);
        int endPage = Math.Min(UserTotalPages, UserCurrentPage + 2);
        for (int page = startPage; page <= endPage; page++)
        {
            UserPageNumbers.Add(new PageNumberItem
            {
                PageNumber = page,
                IsActive = page == UserCurrentPage
            });
        }

        UserTargetPageInput = UserCurrentPage.ToString();
    }
}
