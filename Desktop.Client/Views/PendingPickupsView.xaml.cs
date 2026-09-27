using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class PendingPickupsView : UserControl
{
    private ICollectionView? _view;
    private PendingPickupsViewModel? _vm;

    public PendingPickupsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel(DataContext as PendingPickupsViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        AttachViewModel(e.NewValue as PendingPickupsViewModel);
    }

    private void AttachViewModel(PendingPickupsViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm) && _view != null) return;

        DetachViewModel();
        _vm = vm;

        if (_vm == null) return;

        var targetVm = _vm;
        _view = CollectionViewSource.GetDefaultView(targetVm.Pickups);
        _view.Filter = o => o is PendingPickupClientDto p && targetVm != null && targetVm.MatchesSearch(p);
        PickupsList.ItemsSource = _view;
        _view.Refresh();
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void DetachViewModel()
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }

        if (_view != null)
        {
            _view.Filter = null;
            _view = null;
        }

        PickupsList.ItemsSource = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PendingPickupsViewModel.SearchQuery))
        {
            _view?.Refresh();
        }
    }
}