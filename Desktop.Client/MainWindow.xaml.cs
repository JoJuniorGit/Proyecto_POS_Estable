using System.ComponentModel;
using System.Windows;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;

namespace Desktop.Client;

public partial class MainWindow : Window
{
    private readonly IDialogService _dialogService;

    public MainWindow(MainViewModel viewModel, IDialogService dialogService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _dialogService = dialogService;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (App.IsShutdownRequested) return;

        // Si hay un diálogo modal abierto (ventana o DialogHost), avisar antes de cerrar
        // para evitar la pérdida accidental de información sin confirmar (p. ej. un adelanto a medio llenar).
        if (_dialogService.HasOpenModalDialog)
        {
            var result = MessageBox.Show(this,
                "Hay un diálogo abierto con información posiblemente sin guardar.\n\n" +
                "¿Desea cerrar la aplicación de todos modos? Los datos no confirmados del diálogo se perderán.",
                "Confirmar cierre",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            App.ShutdownReason = "Cierre de la ventana principal con diálogo abierto (confirmado por el usuario)";
        }
        else
        {
            App.ShutdownReason = "Cierre de la ventana principal";
        }

        // El usuario confirmó el cierre: evita que el Shutdown posterior vuelva a preguntar.
        App.IsShutdownRequested = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Red de seguridad: con ShutdownMode=OnMainWindowClose esto es redundante, pero
        // garantiza el apagado completo de la aplicación (y de cualquier ventana restante,
        // como el escáner) aunque el modo de cierre cambie en el futuro.
        Application.Current.Shutdown();
    }
}