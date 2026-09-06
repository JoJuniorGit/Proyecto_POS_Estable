using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

/// <summary>
/// Contrato desacoplado para notificar a clientes y observadores sobre cambios
/// en el catálogo de métodos de pago (creación, edición, alternancia de tipo o eliminación).
/// </summary>
public interface IPaymentMethodNotifier
{
    Task NotifyPaymentMethodsUpdatedAsync();
}
