using System;
using System.Windows;
using System.Windows.Controls;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class PendingPickupsView : UserControl
{
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
        if (ReferenceEquals(_vm, vm)) return;

        DetachViewModel();
        _vm = vm;

        if (_vm == null) return;

        _vm.RequestClipboardCopy += OnRequestClipboardCopy;
    }

    private void DetachViewModel()
    {
        if (_vm == null) return;

        _vm.RequestClipboardCopy -= OnRequestClipboardCopy;
        _vm = null;
    }

    private void OnRequestClipboardCopy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // The Windows clipboard is a shared OS resource that another process may be
            // holding; a failed copy must never break the dispatch workflow.
            Core.Logging.ClientStateLogger.LogWarning(
                $"[PendingPickupsView] Error al copiar al portapapeles: {ex.Message}");
        }
    }
}
