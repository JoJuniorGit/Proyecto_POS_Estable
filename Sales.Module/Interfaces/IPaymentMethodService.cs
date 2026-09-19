using Sales.Module.DTOs;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

public interface IPaymentMethodService
{
    Task<IEnumerable<PaymentMethodDto>> GetActiveMethodsAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<PaymentMethodDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<PaymentMethodDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<PaymentMethodDto> CreateAsync(PaymentMethodDto dto, CancellationToken cancellationToken = default);
    Task<PaymentMethodDto> UpdateAsync(PaymentMethodDto dto, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
