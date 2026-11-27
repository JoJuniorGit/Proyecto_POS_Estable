using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

/// <summary>
/// F4-WU1 (Paso 14): cliente HTTP del Estado de Resultados. La lectura usa el contrato
/// Admin/Manager de la API y las reglas de comisión el contrato Admin-only de F3.
/// </summary>
public interface IProfitabilityClientService
{
    Task<ProfitabilitySummaryClientDto> GetSummaryAsync(
        ProfitabilityClientFilter filter,
        CancellationToken cancellationToken = default);

    Task<ProfitabilityBreakdownClientDto> GetBreakdownAsync(
        ProfitabilityClientFilter filter,
        CancellationToken cancellationToken = default);

    Task<ProfitabilityClientExport> ExportAsync(
        ProfitabilityClientFilter filter,
        string format,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaymentMethodFeeRuleClientDto>> GetFeeRulesAsync(
        int? paymentMethodId,
        bool? activeOnly,
        CancellationToken cancellationToken = default);

    Task<PaymentMethodFeeRuleClientDto> CreateFeeRuleAsync(
        CreatePaymentMethodFeeRuleClientRequest request,
        CancellationToken cancellationToken = default);

    Task<PaymentMethodFeeRuleClientDto> UpdateFeeRuleAsync(
        int id,
        UpdatePaymentMethodFeeRuleClientRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteFeeRuleAsync(int id, CancellationToken cancellationToken = default);
}
