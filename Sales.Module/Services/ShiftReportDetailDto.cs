namespace Sales.Module.Services;

/// <summary>
/// DTO for shift report detail lines. Used by both the report endpoint and the close response.
/// </summary>
public class ShiftReportDetailDto
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public string Currency { get; set; } = "Bs.S";
    public decimal DeclaredAmount { get; set; }
    public decimal SystemAmount { get; set; }
    public decimal Difference { get; set; }
    public string Status { get; set; } = "Balanced";
}
