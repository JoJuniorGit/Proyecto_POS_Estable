using System.Windows;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class VariantManagementDialog : Window
{
    public VariantManagementViewModel ViewModel { get; }

    public VariantManagementDialog(VariantManagementViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ViewModel.RequestClose = (result) =>
        {
            DialogResult = result;
            Close();
        };

        PreviewKeyDown += VariantManagementDialog_PreviewKeyDown;
        Closed += (s, e) => ViewModel.Dispose();
    }

    private void VariantManagementDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ViewModel.IsLinkingPanelOpen)
            {
                ViewModel.IsLinkingPanelOpen = false;
                e.Handled = true;
                return;
            }

            DialogResult = true;
            Close();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsLinkingPanelOpen)
        {
            if (e.Key == Key.Enter && ViewModel.HasSelectedCandidates && CandidatesListBox.IsKeyboardFocusWithin)
            {
                _ = ViewModel.LinkSelectedCandidatesAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Space && CandidatesListBox.IsKeyboardFocusWithin)
            {
                if (CandidatesListBox.SelectedItem is CandidateProductItemViewModel selectedCandidate)
                {
                    selectedCandidate.IsSelected = !selectedCandidate.IsSelected;
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
