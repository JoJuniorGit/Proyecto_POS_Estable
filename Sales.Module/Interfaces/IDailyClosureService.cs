using Sales.Module.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

public class ExpectedTotalDto
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal ExpectedAmountBsS { get; set; }
}

public interface IDailyClosureService
{
    Task<List<ExpectedTotalDto>> GetExpectedTotalsByPaymentMethodAsync(DateTime dateUtc);
    Task<DailyClosure> CreateClosureAsync(DailyClosure closure);
    Task<DailyClosure?> GetClosureAsync(int id);
}
