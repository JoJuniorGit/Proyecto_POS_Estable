using Sales.Module.DTOs;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

/// <summary>
/// Paso 14 (F3-S1): CRUD de las reglas de comisión de medios de pago. La baja es lógica porque
/// los cobros ya realizados referencian la regla y conservan su snapshot de comisión.
/// </summary>
public interface IPaymentMethodFeeRuleService
{
    Task<IReadOnlyList<PaymentMethodFeeRuleDto>> GetAllAsync(
        int? paymentMethodId,
        bool? activeOnly,
        CancellationToken cancellationToken = default);

    Task<PaymentMethodFeeRuleDto> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<PaymentMethodFeeRuleDto> CreateAsync(
        CreatePaymentMethodFeeRuleRequest request,
        int? actingUserId,
        CancellationToken cancellationToken = default);

    Task<PaymentMethodFeeRuleDto> UpdateAsync(
        int id,
        UpdatePaymentMethodFeeRuleRequest request,
        int? actingUserId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        int id,
        CancellationToken cancellationToken = default);
}
