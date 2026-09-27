namespace Sales.Module.Services;

public class ShiftReportDetailDto
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public string Currency { get; set; } = PaymentMethodCurrencyResolver.LocalCurrency;
    public decimal DeclaredAmount { get; set; }
    public decimal SystemAmount { get; set; }
    public decimal Difference { get; set; }
    public string Status { get; set; } = ClosureStatus.Balanced;
}
