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

    private bool _isShuttingDown;

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (App.IsShutdownRequested && _isShuttingDown) return;

        bool hasUncommittedCart = (DataContext as MainViewModel)?.HasUncommittedCartItems == true;
        bool hasOpenModal = _dialogService.HasOpenModalDialog;

        // Si hay un diálogo modal abierto (ventana o DialogHost), avisar antes de cerrar
        // para evitar la pérdida accidental de información sin confirmar (p. ej. un adelanto a medio llenar).
        if (_dialogService.HasOpenModalDialog || hasUncommittedCart)
        {
            string message;
            if (hasOpenModal && hasUncommittedCart)
            {
                message = "Hay un diálogo abierto y productos sin cobrar en el carrito de la venta actual.\n\n" +
                    "¿Desea cerrar la aplicación de todos modos? Los datos no confirmados del diálogo se perderán y la venta en curso quedará pendiente.";
            }
            else if (hasOpenModal)
            {
                message = "Hay un diálogo abierto con información posiblemente sin guardar.\n\n" +
                    "¿Desea cerrar la aplicación de todos modos? Los datos no confirmados del diálogo se perderán.";
            }
            else
            {
                message = "Hay productos sin cobrar en el carrito de la venta actual.\n\n" +
                    "¿Desea cerrar la aplicación de todos modos? La venta en curso quedará pendiente y podrá recuperarse en el próximo inicio si el cierre no fue confirmado.";
            }

            var result = MessageBox.Show(this,
                message,
                "Confirmar cierre",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            if (hasOpenModal && hasUncommittedCart)
            {
                App.ShutdownReason = "Cierre de la ventana principal con diálogo abierto y carrito con productos (confirmado por el usuario)";
            }
            else if (hasUncommittedCart)
            {
                App.ShutdownReason = "Cierre de la ventana principal con carrito con productos (confirmado por el usuario)";
            }
            else
            {
                App.ShutdownReason = "Cierre de la ventana principal con diálogo abierto (confirmado por el usuario)";
            }
        }
        else
        {
            App.ShutdownReason = "Cierre de la ventana principal";
        }

        if (!_isShuttingDown)
        {
            e.Cancel = true;
            _isShuttingDown = true;
            App.IsShutdownRequested = true;

            if (Application.Current is App app)
            {
                await app.StopServicesAsync();
            }

            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Red de seguridad: con ShutdownMode=OnMainWindowClose esto es redundante, pero
        // garantiza el apagado completo de la aplicación (y de cualquier ventana restante,
        // como el escáner) aunque el modo de cierre cambie en el futuro.
        Application.Current?.Shutdown();
    }
}