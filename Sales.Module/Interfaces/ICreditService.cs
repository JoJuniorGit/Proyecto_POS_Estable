using Core.DTOs;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

/// <summary>
/// F2a (Paso 11, S1/S2): perfil crediticio y evaluación de solvencia en tiempo real del
/// cliente. El cierre transaccional de la venta a crédito vive en el motor de venta
/// (<c>SalesService.CompleteCreditSaleAsync</c>); este servicio es la fuente única del
/// cupo disponible, la utilización, la mora dinámica y los bloqueos de elegibilidad.
/// </summary>
public interface ICreditService
{
    /// <summary>
    /// Perfil crediticio del cliente con deuda vigente, disponible, utilización y mora
    /// calculados en tiempo real. Cliente inexistente → KeyNotFoundException (404).
    /// </summary>
    Task<CreditProfileDto> GetCreditProfileAsync(int customerId, System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// Evalúa si el cliente puede financiar <paramref name="amountUSD"/> con su perfil y
    /// obligaciones vigentes. Devuelve elegibilidad + bloqueos en español + cupo disponible;
    /// un cliente inexistente bloquea (no lanza). Sin bypass de sobregiro (T3 autoriza).
    /// </summary>
    Task<CreditEligibilityResultDto> EvaluateEligibilityAsync(int customerId, decimal amountUSD, CancellationToken cancellationToken = default);

    /// <summary>
    /// T3a (F2b, S3/S6): estado de cuenta dual del cliente (perfil + cartera abierta/cerrada con
    /// equivalente Bs.S del saldo a la tasa del día + historial de abonos con FX realizado).
    /// Cliente inexistente → KeyNotFoundException (404).
    /// </summary>
    Task<CreditStatementDto> GetCreditStatementAsync(int customerId, System.Threading.CancellationToken cancellationToken = default);
}
