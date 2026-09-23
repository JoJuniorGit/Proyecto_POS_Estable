using System.Windows;
using System.Windows.Controls;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class DailyClosureView : UserControl
{
    private DailyClosureViewModel? _boundViewModel;

    public DailyClosureView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookViewModel(DataContext as DailyClosureViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DailyClosureViewModel oldVm)
        {
            if (ReferenceEquals(_boundViewModel, oldVm))
            {
                UnhookViewModel();
            }
        }

        if (e.NewValue is DailyClosureViewModel newVm)
        {
            HookViewModel(newVm);
        }
    }

    private void HookViewModel(DailyClosureViewModel? vm)
    {
        if (vm == null || ReferenceEquals(_boundViewModel, vm)) return;

        UnhookViewModel();
        _boundViewModel = vm;
        _boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateBlindClosingVisibility(_boundViewModel.IsBlindClosing);
    }

    private void UnhookViewModel()
    {
        if (_boundViewModel != null)
        {
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _boundViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DailyClosureViewModel.IsBlindClosing) && _boundViewModel != null)
        {
            UpdateBlindClosingVisibility(_boundViewModel.IsBlindClosing);
        }
    }

    private void UpdateBlindClosingVisibility(bool isBlind)
    {
        if (ExpectedColumn != null)
            ExpectedColumn.Visibility = isBlind ? Visibility.Collapsed : Visibility.Visible;
            
        if (ExpectedTotalPanel != null)
            ExpectedTotalPanel.Visibility = isBlind ? Visibility.Collapsed : Visibility.Visible;
            
        if (DifferenceColumn != null)
            DifferenceColumn.Visibility = isBlind ? Visibility.Collapsed : Visibility.Visible;
            
        if (DifferenceTotalPanel != null)
            DifferenceTotalPanel.Visibility = isBlind ? Visibility.Collapsed : Visibility.Visible;
    }
}
