using System;
using System.Collections.Generic;
using Sales.Module.Services;

namespace Sales.Module.DTOs;

public class CloseShiftRequest
{
    public List<DeclaredAmountDto> DeclaredAmounts { get; set; } = new();
}

public class DeclaredAmountDto
{
    public int PaymentMethodId { get; set; }
    public decimal Amount { get; set; }
}

public class ShiftReportDto
{
    public int ShiftId { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public string CashierCedula { get; set; } = string.Empty;
    public DateTime ClosedAt { get; set; }
    public decimal ExchangeRate { get; set; }
    public List<ShiftReportDetailDto> Details { get; set; } = new();
}