using Sales.Module.DTOs;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

/// <summary>
/// 8.159-T1 (CLEAN-04, REQ-CHC-01): cálculo del checkout-preview extraído del controlador.
/// Encapsula el fetch de la venta, la resolución de tasa anclada (8.152/SEC-08) y la
/// matemática espejo del completar venta (totales, pagos mixtos, redondeo fiscal, vuelto).
/// </summary>
public interface ICheckoutCalculationService
{
    Task<CheckoutPreviewResponse> CalculatePreviewAsync(int saleId, CheckoutPreviewRequest request, CancellationToken cancellationToken = default);
}
