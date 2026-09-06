using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public class PageNumberItem
{
    public int PageNumber { get; set; }
    public bool IsActive { get; set; }
}

public partial class InventoryViewModel
{
    [RelayCommand]
    public async Task Sort(string column)
    {
        if (string.IsNullOrWhiteSpace(column)) return;

        if (SortBy.Equals(column, StringComparison.OrdinalIgnoreCase))
        {
            IsSortDescending = !IsSortDescending;
        }
        else
        {
            SortBy = column.ToLower().Trim();
            IsSortDescending = false;
        }

        OnPropertyChanged(nameof(IsSortedByName));
        OnPropertyChanged(nameof(IsSortedBySku));
        OnPropertyChanged(nameof(IsSortedByStock));
        OnPropertyChanged(nameof(IsSortedByCost));
        OnPropertyChanged(nameof(IsSortedByPrice));

        await LoadDataAsync(false, targetPage: 1);
    }

    [RelayCommand]
    private async Task FirstPage()
    {
        if (CanGoFirst)
        {
            await LoadDataAsync(false, targetPage: 1);
        }
    }

    [RelayCommand]
    private async Task PreviousPage()
    {
        if (CanGoPrevious)
        {
            await LoadDataAsync(false, targetPage: CurrentPage - 1);
        }
    }

    [RelayCommand]
    private async Task NextPage()
    {
        if (CanGoNext)
        {
            await LoadDataAsync(false, targetPage: CurrentPage + 1);
        }
    }

    [RelayCommand]
    private async Task LastPage()
    {
        if (CanGoLast)
        {
            await LoadDataAsync(false, targetPage: TotalPages);
        }
    }

    [RelayCommand]
    private async Task GoToPage(int page)
    {
        if (page >= 1 && page <= TotalPages && page != CurrentPage)
        {
            await LoadDataAsync(false, targetPage: page);
        }
    }

    [RelayCommand]
    private async Task SubmitGoToPage()
    {
        if (int.TryParse(TargetPageInput, out int target) && TotalPages > 0)
        {
            int clamped = Math.Clamp(target, 1, TotalPages);
            if (clamped != CurrentPage)
            {
                await LoadDataAsync(false, targetPage: clamped);
            }
            else
            {
                TargetPageInput = CurrentPage.ToString();
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

        if (TotalPages <= 0 || TotalCount == 0)
        {
            CurrentPage = 0;
            PageSummary = "Página 0 de 0 (0 productos)";
            TargetPageInput = "0";
            NotifyPaginationCanExecute();
            return;
        }

        if (CurrentPage <= 0) CurrentPage = 1;
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;

        int startPage = Math.Max(1, CurrentPage - 2);
        int endPage = Math.Min(TotalPages, CurrentPage + 2);

        for (int p = startPage; p <= endPage; p++)
        {
            PageNumbers.Add(new PageNumberItem
            {
                PageNumber = p,
                IsActive = (p == CurrentPage)
            });
        }

        TargetPageInput = CurrentPage.ToString();
        NotifyPaginationCanExecute();
    }

    public void NotifyPaginationCanExecute()
    {
        OnPropertyChanged(nameof(CanGoFirst));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoLast));
    }

    private async Task RestartSearchTimerAsync()
    {
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _cancellationTokenSource, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var token = newCts.Token;

        try
        {
            var text = _searchText?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(text))
            {
                await LoadDataAsync(false, targetPage: 1, token: token);
                return;
            }

            if (text.Length < 1)
            {
                return; 
            }

            // 40ms Debounce para escritura rápida
            await Task.Delay(40, token);
            
            await LoadDataAsync(false, targetPage: 1, token: token);
        }
        catch (OperationCanceledException) { }
    }
}
