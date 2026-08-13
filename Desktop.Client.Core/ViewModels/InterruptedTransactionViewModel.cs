using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Logging;
using System.Windows;

namespace Desktop.Client.ViewModels;

public partial class InterruptedTransactionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _operationName = "Cobro / Transacción";

    [ObservableProperty]
    private string _message = "La conexión con el servidor se interrumpió durante la operación. La red ha sido restablecida. Por favor, verifique el estado de caja y presione el botón de cobro nuevamente.";

    public InterruptedTransactionViewModel(string operationName, string customMessage = "")
    {
        if (!string.IsNullOrWhiteSpace(operationName))
        {
            OperationName = operationName;
        }

        if (!string.IsNullOrWhiteSpace(customMessage))
        {
            Message = customMessage;
        }

        // AUDIT LOG: Log that auto-replay was suppressed and control was delegated to Cashier
        ClientStateLogger.LogAuditSuppressedReplay(OperationName);
    }

    [RelayCommand]
    private void Dismiss(Window window)
    {
        window?.Close();
    }
}
