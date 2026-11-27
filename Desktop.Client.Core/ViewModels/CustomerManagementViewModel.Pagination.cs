using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Paginación avanzada de clientes con el mismo lenguaje del catálogo: primera/última página,
/// números relativos, resumen e ir a página. La consulta y el total siguen siendo server-side.
/// </summary>
public partial class CustomerManagementViewModel
{
    [ObservableProperty]
    private string _targetPageInput = "1";

    public ObservableCollection<PageNumberItem> PageNumbers { get; } = new();

    [RelayCommand]
    private async Task FirstPageAsync()
    {
        if (!CanGoToFirstPage) return;
        CurrentPage = 1;
        await LoadCustomersAsync();
    }

    [RelayCommand]
    private async Task LastPageAsync()
    {
        if (!CanGoToLastPage) return;
        CurrentPage = TotalPages;
        await LoadCustomersAsync();
    }

    [RelayCommand]
    private async Task GoToPageAsync(int page)
    {
        if (page >= 1 && page <= TotalPages && page != CurrentPage)
        {
            CurrentPage = page;
            await LoadCustomersAsync();
        }
    }

    [RelayCommand]
    private async Task SubmitGoToPageAsync()
    {
        if (int.TryParse(TargetPageInput, out int target) && TotalPages > 0)
        {
            int clamped = Math.Clamp(target, 1, TotalPages);
            if (clamped != CurrentPage)
            {
                CurrentPage = clamped;
                await LoadCustomersAsync();
            }
            else
            {
                UpdatePageNumbers();
            }
        }
        else
        {
            TargetPageInput = CurrentPage > 0 ? CurrentPage.ToString() : "1";
        }
    }

    public void UpdatePageNumbers()
    {
        PageNumbers.Clear();

        if (TotalPages <= 0)
        {
            TargetPageInput = "0";
            return;
        }

        if (CurrentPage <= 0) CurrentPage = 1;
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;

        int startPage = Math.Max(1, CurrentPage - 2);
        int endPage = Math.Min(TotalPages, CurrentPage + 2);
        for (int page = startPage; page <= endPage; page++)
        {
            PageNumbers.Add(new PageNumberItem
            {
                PageNumber = page,
                IsActive = page == CurrentPage
            });
        }

        TargetPageInput = CurrentPage.ToString();
    }
}
