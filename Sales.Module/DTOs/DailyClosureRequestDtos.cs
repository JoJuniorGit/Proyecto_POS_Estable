using System;
using System.Collections.Generic;

namespace Sales.Module.DTOs;

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