using Sales.Module.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

public interface IPaymentMethodService
{
    Task<IEnumerable<PaymentMethod>> GetActiveMethodsAsync();
    Task<IEnumerable<PaymentMethod>> GetAllAsync();
    Task<PaymentMethod> GetByIdAsync(int id);
    Task<PaymentMethod> CreateAsync(PaymentMethod method);
    Task<PaymentMethod> UpdateAsync(PaymentMethod method);
    Task DeleteAsync(int id); // Logical delete
}
