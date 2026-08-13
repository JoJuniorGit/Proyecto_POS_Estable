using System.Windows;
using System.Windows.Controls;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class DailyClosureView : UserControl
{
    public DailyClosureView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is DailyClosureViewModel vm)
        {
            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(DailyClosureViewModel.IsBlindClosing))
                {
                    UpdateBlindClosingVisibility(vm.IsBlindClosing);
                }
            };
            UpdateBlindClosingVisibility(vm.IsBlindClosing);
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
