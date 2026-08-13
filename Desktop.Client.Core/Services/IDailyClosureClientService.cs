using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class ExpectedTotalDto
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal ExpectedAmountBsS { get; set; }
}

public class DailyClosureDto
{
    public int Id { get; set; }
    public DateTime ClosureDate { get; set; }
    public string? UserId { get; set; }
    public decimal TotalExpectedBsS { get; set; }
    public decimal TotalActualBsS { get; set; }
    public decimal TotalDifferenceBsS { get; set; }
    public string? Observation { get; set; }
    public List<ClosureDetailDto> Details { get; set; } = new();
}

public class ClosureDetailDto
{
    public int Id { get; set; }
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal ExpectedAmountBsS { get; set; }
    public decimal ActualAmountBsS { get; set; }
    public decimal DifferenceBsS { get; set; }
}

public class CreateClosureRequest
{
    public DateTime ClosureDate { get; set; }
    public string? UserId { get; set; } = "Admin";
    public string? Observation { get; set; }
    public List<CreateClosureDetailRequest> Details { get; set; } = new();
}

public class CreateClosureDetailRequest
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal ExpectedAmountBsS { get; set; }
    public decimal ActualAmountBsS { get; set; }
}

public interface IDailyClosureClientService
{
    Task<List<ExpectedTotalDto>> GetExpectedTotalsAsync(DateTime dateUtc);
    Task<DailyClosureDto> CreateClosureAsync(CreateClosureRequest request);
}
