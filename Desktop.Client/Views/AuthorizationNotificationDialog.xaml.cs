using System;
using System.ComponentModel;
using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

/// <summary>
/// 8.150 (T10, design D7): modal interrumpente de notificaciones admin. Esta bindeado a la cola
/// del ViewModel y se cierra solo cuando la cola queda vacia; los avisos de carrera/expiracion
/// permanecen visibles hasta que el administrador los dispone. No dispone el ViewModel: su ciclo
/// de vida pertenece a la sesion (MainViewModel/DI).
/// </summary>
public partial class AuthorizationNotificationDialog : Window
{
    private bool _closeRequested;

    public AuthorizationNotificationViewModel ViewModel { get; }

    public AuthorizationNotificationDialog(AuthorizationNotificationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ViewModel.StateChanged += OnViewModelStateChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.StateChanged -= OnViewModelStateChanged;
        base.OnClosed(e);
    }

    private void OnViewModelStateChanged(object? sender, EventArgs e)
    {
        // Close-on-others-resolved / cola drenada: sin pendientes y sin aviso, el modal se cierra.
        if (!ViewModel.HasPendingNotifications && ViewModel.Notice == null)
        {
            RequestClose();
        }
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        // Modal interruptor: no se cierra manualmente mientras haya solicitudes pendientes; el
        // apagado de la aplicacion siempre puede cerrarlo.
        if (ViewModel.HasPendingNotifications && !App.IsShutdownRequested)
        {
            e.Cancel = true;
        }
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        if (_closeRequested)
        {
            DialogResult = false;
        }
    }

    private void RequestClose()
    {
        _closeRequested = true;
        if (IsLoaded)
        {
            DialogResult = false;
        }
    }
}
