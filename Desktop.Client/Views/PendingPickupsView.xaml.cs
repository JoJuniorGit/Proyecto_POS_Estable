using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class PendingPickupsView : UserControl
{
    private ListCollectionView? _view;
    private PendingPickupsViewModel? _vm;

    public PendingPickupsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PendingPickupsViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = e.NewValue as PendingPickupsViewModel;
        if (_vm == null)
        {
            _view = null;
            PickupsList.ItemsSource = null;
            return;
        }

        _view = new ListCollectionView(_vm.Pickups)
        {
            Filter = o => o is PendingPickupClientDto p && _vm.MatchesSearch(p)
        };
        PickupsList.ItemsSource = _view;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PendingPickupsViewModel.SearchQuery) ||
            e.PropertyName == nameof(PendingPickupsViewModel.Pickups))
        {
            _view?.Refresh();
        }
    }
}