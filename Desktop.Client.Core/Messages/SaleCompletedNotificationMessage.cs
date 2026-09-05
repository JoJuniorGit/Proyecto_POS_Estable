using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Desktop.Client.Messages;

/// <summary>
/// Mensaje emitido inmediatamente después de que una venta se completa exitosamente en el servidor.
/// Utilizado por ViewModels secundarios (como SalesHistoryViewModel) para invalidar cachés
/// o recargar datos de forma dirigida sin realizar consultas HTTP redundantes.
/// </summary>
public class SaleCompletedNotificationMessage : ValueChangedMessage<int>
{
    public SaleCompletedNotificationMessage(int invoiceNumber) : base(invoiceNumber)
    {
    }
}
